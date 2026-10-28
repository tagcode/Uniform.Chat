namespace Uniform.Matrix.Server.Federation;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>Events other servers send in transactions: their senders' keys are gathered, and when they build on events this server does not have, those are asked for with <see href="https://spec.matrix.org/latest/server-server-api/#post_matrixfederationv1get_missing_eventsroomid">get_missing_events</see> and accepted first.</summary>
/// <remarks>At most <see cref="MaxMissing"/> missing events are asked for per event, one level deep. When previous or auth events are still missing after that, the sending server is asked for the <see href="https://spec.matrix.org/latest/server-server-api/#get_matrixfederationv1state_idsroomid">state before the event</see>; the events of that state and their auth chain that are not here are fetched one by one, and the event is checked against that state.</remarks>
public sealed class RemoteEvents
{
    /// <summary>Most missing events asked for at once.</summary>
    public const int MaxMissing = 20;
    /// <summary>Most events fetched at once when filling in state.</summary>
    private const int ParallelFetches = 10;
    private readonly EventSender sender;
    private readonly RemoteServerKeys keys;
    private readonly FederationClient federation;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ILogger<RemoteEvents> logger;

    /// <summary>Create the service.</summary>
    public RemoteEvents(EventSender sender, RemoteServerKeys keys, FederationClient federation, IDbContextFactory<ServerDbContext> contexts, ILogger<RemoteEvents> logger)
    {
        // Store dependencies
        this.sender = sender;
        this.keys = keys;
        this.federation = federation;
        this.contexts = contexts;
        this.logger = logger;
    }

    /// <summary>Accept an event <paramref name="origin"/> sent, after the events before it that are missing here; return its ID, if known, and why it was rejected, or null when stored.</summary>
    public async Task<(string? EventId, string? Error)> AcceptAsync(string origin, JsonObject pdu, CancellationToken cancellationToken)
    {
        // Missing events before it first, then the state before it when still not all is here
        await FillGapAsync(origin, pdu, cancellationToken);
        IReadOnlyDictionary<(string Type, string StateKey), string>? reported = await FetchStateBeforeAsync(origin, pdu, cancellationToken);
        // Return the result of the event itself
        return await AcceptOneAsync(pdu, cancellationToken, reported);
    }

    /// <summary>Ask <paramref name="origin"/> for the state before <paramref name="pdu"/> when its previous or auth events are not here, fetch and store the events of it that are missing; return the state, or null when not needed or not available.</summary>
    private async Task<IReadOnlyDictionary<(string Type, string StateKey), string>?> FetchStateBeforeAsync(string origin, JsonObject pdu, CancellationToken cancellationToken)
    {
        // Assert a room here and something missing
        string? roomId = pdu["room_id"]?.GetValue<string>();
        if (roomId is null) return null;
        List<string> referenced = [.. Ids(pdu["prev_events"]).Concat(Ids(pdu["auth_events"]))];
        if ((await sender.FindUnknownAsync(referenced, cancellationToken)).Count == 0) return null;
        RoomVersion? version;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            version = RoomVersion.Find(await db.Rooms.AsNoTracking().Where(r => r.RoomId == roomId).Select(r => r.Version).SingleOrDefaultAsync(cancellationToken));
        }
        if (version is null) return null;
        string eventId = EventFormat.ComputeEventId(pdu, version);

        // State and auth chain IDs at the event
        JsonNode? answer;
        try
        {
            answer = await federation.SendAsync(HttpMethod.Get, origin, $"/_matrix/federation/v1/state_ids/{Uri.EscapeDataString(roomId)}?event_id={Uri.EscapeDataString(eventId)}", null, cancellationToken);
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Cannot get the state before {EventId} of {RoomId} from {Origin}: {Error}", eventId, roomId, origin, ex.Message);
            return null;
        }
        List<string> stateIds = [.. Ids(answer?["pdu_ids"])];
        List<string> chainIds = [.. Ids(answer?["auth_chain_ids"])];

        // The missing ones fetched, checked and stored
        IReadOnlyList<string> unknown = await sender.FindUnknownAsync([.. stateIds, .. chainIds, .. Ids(pdu["auth_events"])], cancellationToken);
        Dictionary<string, JsonObject> fetched = await FetchEventsAsync(origin, roomId, version, unknown, cancellationToken);
        await sender.StoreFetchedAsync(roomId, fetched, cancellationToken);

        // The state as stored events by type and state key
        IReadOnlyList<string> stillUnknown = await sender.FindUnknownAsync(stateIds, cancellationToken);
        Dictionary<(string Type, string StateKey), string> state = [];
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            List<string> present = [.. stateIds.Except(stillUnknown)];
            foreach (string[] batch in present.Chunk(500))
            {
                foreach (EventRow row in await db.Events.IgnoreQueryFilters().AsNoTracking().Where(e => batch.Contains(e.EventId) && e.RoomId == roomId && e.StateKey != null).ToListAsync(cancellationToken)) state[(row.Type, row.StateKey!)] = row.EventId;
            }
        }
        logger.LogInformation("Fetched {Count} events of the state before {EventId} of {RoomId} from {Origin}; {Missing} could not be had", fetched.Count, eventId, roomId, origin, stillUnknown.Count);
        // Return state
        return state;
    }

    /// <summary>Fetch events from <paramref name="origin"/> one by one, several at a time; return those of the room with valid hashes and signatures by ID.</summary>
    private async Task<Dictionary<string, JsonObject>> FetchEventsAsync(string origin, string roomId, RoomVersion version, IReadOnlyList<string> eventIds, CancellationToken cancellationToken)
    {
        // Each event, its sender's keys known first
        ConcurrentDictionary<string, JsonObject> fetched = new();
        await Parallel.ForEachAsync(eventIds, new ParallelOptions { MaxDegreeOfParallelism = ParallelFetches, CancellationToken = cancellationToken }, async (eventId, token) =>
        {
            try
            {
                JsonNode? answer = await federation.SendAsync(HttpMethod.Get, origin, $"/_matrix/federation/v1/event/{Uri.EscapeDataString(eventId)}", null, token);
                if ((answer?["pdus"] as JsonArray)?.FirstOrDefault() is JsonObject pdu) fetched[eventId] = pdu;
            }
            catch (MatrixException ex)
            {
                logger.LogDebug("Cannot fetch {EventId} from {Origin}: {Error}", eventId, origin, ex.Message);
            }
        });
        await keys.PrefetchAsync(fetched.Values.Select(p => UserId.TryParse(p["sender"]?.GetValue<string>(), out UserId user) ? user.ServerName : null).OfType<string>(), cancellationToken);

        // The ones that are what they claim
        Dictionary<string, JsonObject> valid = [];
        foreach ((string eventId, JsonObject pdu) in fetched)
        {
            pdu.Remove("unsigned");
            bool ofRoom = pdu["room_id"]?.GetValue<string>() == roomId || version.RoomIdIsCreateHash && pdu["type"]?.GetValue<string>() == EventTypes.RoomCreate && RoomVersion.RoomIdOf(eventId) == roomId;
            if (ofRoom && EventFormat.ComputeEventId(pdu, version) == eventId && EventFormat.HasValidContentHash(pdu) && await IsSignedAsync(pdu, version, cancellationToken)) valid[eventId] = pdu;
        }
        // Return valid events
        return valid;
    }

    /// <summary>Return whether the sender's server signed an event with a key it has.</summary>
    private async Task<bool> IsSignedAsync(JsonObject pdu, RoomVersion version, CancellationToken cancellationToken)
    {
        // Any valid key of the sender's server
        if (!UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId eventSender)) return false;
        foreach (string keyId in (pdu["signatures"]?[eventSender.ServerName] as JsonObject ?? []).Select(k => k.Key))
        {
            if (await keys.GetVerifyKeyAsync(eventSender.ServerName, keyId, cancellationToken) is string key && EventFormat.HasValidSignature(pdu, version, eventSender.ServerName, keyId, key)) return true;
        }
        // On error not signed
        return false;
    }

    /// <summary>Return the strings of a JSON array of IDs.</summary>
    private static IEnumerable<string> Ids(JsonNode? array) => (array as JsonArray ?? []).Select(e => e is JsonValue v && v.TryGetValue(out string? id) ? id : null).OfType<string>();

    /// <summary>Ask <paramref name="origin"/> for the events between this server's latest ones and <paramref name="pdu"/>, when it builds on events not here, and accept them oldest first.</summary>
    private async Task FillGapAsync(string origin, JsonObject pdu, CancellationToken cancellationToken)
    {
        // Assert a room here and previous events that are missing
        string? roomId = pdu["room_id"]?.GetValue<string>();
        List<string> previous = [.. (pdu["prev_events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>()];
        if (roomId is null || previous.Count == 0) return;
        List<string> ours;
        string eventId;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken) is not RoomRow room || RoomVersion.Find(room.Version) is not RoomVersion version) return;
            int known = await db.Events.IgnoreQueryFilters().CountAsync(e => e.RoomId == roomId && previous.Contains(e.EventId), cancellationToken);
            if (known == previous.Count) return;
            ours = await db.ForwardExtremities.AsNoTracking().Where(e => e.RoomId == roomId).Select(e => e.EventId).ToListAsync(cancellationToken);
            eventId = EventFormat.ComputeEventId(pdu, version);
        }

        // What lies between our latest events and this one, oldest first
        JsonNode? answer;
        try
        {
            JsonObject request = new() { ["earliest_events"] = new JsonArray([.. ours.Select(e => (JsonNode)e)]), ["latest_events"] = new JsonArray(eventId), ["limit"] = MaxMissing, ["min_depth"] = 0 };
            answer = await federation.SendAsync(HttpMethod.Post, origin, $"/_matrix/federation/v1/get_missing_events/{Uri.EscapeDataString(roomId)}", request, cancellationToken);
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Cannot get the events missing before an event of {RoomId} from {Origin}: {Error}", roomId, origin, ex.Message);
            return;
        }
        List<JsonObject> missing = [.. (answer?["events"] as JsonArray ?? []).OfType<JsonObject>().Where(e => e["room_id"]?.GetValue<string>() == roomId).Take(MaxMissing)];
        foreach (JsonObject earlier in missing.OrderBy(e => e["depth"]?.GetValue<long>() ?? 0))
        {
            // The oldest may still build on events not here, as the gap was larger
            IReadOnlyDictionary<(string Type, string StateKey), string>? reported = await FetchStateBeforeAsync(origin, earlier, cancellationToken);
            (string? earlierId, string? error) = await AcceptOneAsync(earlier, cancellationToken, reported);
            if (error is not null) logger.LogInformation("Missing event {EventId} of {RoomId} rejected: {Error}", earlierId, roomId, error);
        }
        // Done
        if (missing.Count > 0) logger.LogInformation("Filled a gap of {Count} events in {RoomId} from {Origin}", missing.Count, roomId, origin);
    }

    /// <summary>Accept one event with the keys of its sender's server, and the state before it as reported when its previous events are not here.</summary>
    private async Task<(string? EventId, string? Error)> AcceptOneAsync(JsonObject pdu, CancellationToken cancellationToken, IReadOnlyDictionary<(string Type, string StateKey), string>? reportedStateBefore = null)
    {
        // Keys the event names
        Dictionary<string, string> senderKeys = [];
        if (UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId eventSender))
        {
            foreach (string keyId in (pdu["signatures"]?[eventSender.ServerName] as JsonObject ?? []).Select(k => k.Key))
            {
                if (await keys.GetVerifyKeyAsync(eventSender.ServerName, keyId, cancellationToken) is string key) senderKeys[keyId] = key;
            }
        }
        // Return the result
        return await sender.AcceptEventAsync(pdu, senderKeys, cancellationToken, reportedStateBefore);
    }
}
