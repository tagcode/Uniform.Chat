namespace Uniform.Matrix.Server.Federation;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>Events other servers send in transactions: their senders' keys are gathered, and when they build on events this server does not have, those are asked for with <see href="https://spec.matrix.org/latest/server-server-api/#post_matrixfederationv1get_missing_eventsroomid">get_missing_events</see> and accepted first.</summary>
/// <remarks>At most <see cref="MaxMissing"/> missing events are asked for per event, one level deep; a gap that is larger stays, and the event is accepted on top of it.</remarks>
public sealed class RemoteEvents
{
    /// <summary>Most missing events asked for at once.</summary>
    public const int MaxMissing = 20;
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
        // Missing events before it first
        await FillGapAsync(origin, pdu, cancellationToken);
        // Return the result of the event itself
        return await AcceptOneAsync(pdu, cancellationToken);
    }

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
            int known = await db.Events.CountAsync(e => e.RoomId == roomId && previous.Contains(e.EventId), cancellationToken);
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
            (string? earlierId, string? error) = await AcceptOneAsync(earlier, cancellationToken);
            if (error is not null) logger.LogInformation("Missing event {EventId} of {RoomId} rejected: {Error}", earlierId, roomId, error);
        }
        // Done
        if (missing.Count > 0) logger.LogInformation("Filled a gap of {Count} events in {RoomId} from {Origin}", missing.Count, roomId, origin);
    }

    /// <summary>Accept one event with the keys of its sender's server.</summary>
    private async Task<(string? EventId, string? Error)> AcceptOneAsync(JsonObject pdu, CancellationToken cancellationToken)
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
        return await sender.AcceptEventAsync(pdu, senderKeys, cancellationToken);
    }
}
