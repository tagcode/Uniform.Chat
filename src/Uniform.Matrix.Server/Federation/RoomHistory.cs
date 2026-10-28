namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>Room history for other servers, which fill gaps with it before they can show new events: <see href="https://spec.matrix.org/latest/server-server-api/#get_matrixfederationv1get_missing_eventsroomid">missing events</see>, <see href="https://spec.matrix.org/latest/server-server-api/#backfilling-and-retrieving-missing-events">backfill</see>, single events and the state at an event.</summary>
/// <remarks>Only servers with a member joined to the room get its history; events are returned in the PDU format they are stored in.</remarks>
public sealed class RoomHistory
{
    private const int MaxEvents = 100;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly RoomAcls acls;

    /// <summary>Create the service.</summary>
    public RoomHistory(IDbContextFactory<ServerDbContext> contexts, RoomAcls acls)
    {
        // Store dependencies
        this.contexts = contexts;
        this.acls = acls;
    }

    /// <summary>Return the events before <paramref name="latest"/> back to <paramref name="earliest"/>, which the asking server has, at most <paramref name="limit"/>, oldest first.</summary>
    public async Task<JsonArray> GetMissingEventsAsync(string origin, string roomId, IReadOnlyCollection<string> earliest, IReadOnlyCollection<string> latest, int limit, long minDepth, CancellationToken cancellationToken)
    {
        // Breadth first from the prev_events of the latest, stopping at what the asker has
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await AssertMemberServerAsync(db, acls, origin, roomId, cancellationToken);
        HashSet<string> seen = [.. earliest, .. latest];
        List<string> next = [.. (await LoadAsync(db, roomId, latest, cancellationToken)).SelectMany(e => PrevEvents(e.Pdu))];
        List<(long Depth, JsonObject Pdu)> found = [];
        while (next.Count > 0 && found.Count < Math.Min(limit, MaxEvents))
        {
            List<(string Id, long Depth, JsonObject Pdu)> batch = [.. (await LoadAsync(db, roomId, [.. next.Where(seen.Add)], cancellationToken)).Where(e => e.Depth >= minDepth)];
            found.AddRange(batch.Take(Math.Min(limit, MaxEvents) - found.Count).Select(e => (e.Depth, e.Pdu)));
            next = [.. batch.SelectMany(e => PrevEvents(e.Pdu))];
        }
        // Return oldest first
        return new JsonArray([.. found.OrderBy(e => e.Depth).Select(e => (JsonNode)e.Pdu)]);
    }

    /// <summary>Return <paramref name="from"/> and the events before them, at most <paramref name="limit"/>, newest first, as <c>backfill</c> answers.</summary>
    public async Task<JsonArray> BackfillAsync(string origin, string roomId, IReadOnlyCollection<string> from, int limit, CancellationToken cancellationToken)
    {
        // Breadth first from the given events themselves
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await AssertMemberServerAsync(db, acls, origin, roomId, cancellationToken);
        HashSet<string> seen = [];
        List<string> next = [.. from];
        List<(long Depth, JsonObject Pdu)> found = [];
        while (next.Count > 0 && found.Count < Math.Min(limit, MaxEvents))
        {
            List<(string Id, long Depth, JsonObject Pdu)> batch = await LoadAsync(db, roomId, [.. next.Where(seen.Add)], cancellationToken);
            found.AddRange(batch.Take(Math.Min(limit, MaxEvents) - found.Count).Select(e => (e.Depth, e.Pdu)));
            next = [.. batch.SelectMany(e => PrevEvents(e.Pdu))];
        }
        // Return newest first
        return new JsonArray([.. found.OrderByDescending(e => e.Depth).Select(e => (JsonNode)e.Pdu)]);
    }

    /// <summary>Return one event of a room the asking server is in.</summary>
    public async Task<JsonObject> GetEventAsync(string origin, string eventId, CancellationToken cancellationToken)
    {
        // The event, then the asker's right to it
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        EventRow row = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken) ?? throw NotFound();
        await AssertMemberServerAsync(db, acls, origin, row.RoomId, cancellationToken);
        // Return event
        return JsonNode.Parse(row.Json)!.AsObject();
    }

    /// <summary>Return the state of a room at an event, that is before it as other servers expect, and the auth chain of that state, as IDs or as events.</summary>
    public async Task<(List<JsonObject> State, List<JsonObject> AuthChain, List<string> StateIds, List<string> AuthChainIds)> GetStateAsync(string origin, string roomId, string eventId, CancellationToken cancellationToken)
    {
        // State group before the event, as other servers read it; the one after for history whose state before is not known
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await AssertMemberServerAsync(db, acls, origin, roomId, cancellationToken);
        EventRow row = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId && e.RoomId == roomId, cancellationToken) ?? throw NotFound();
        List<string> stateIds = [.. (await StateGroups.LoadAsync(db, row.StateGroupBefore != 0 ? row.StateGroupBefore : row.StateGroup, cancellationToken)).Values];
        List<(string Id, long Depth, JsonObject Pdu)> state = await LoadAsync(db, roomId, stateIds, cancellationToken);

        // Auth events reachable from the state, also those that are state themselves
        HashSet<string> seen = [];
        List<(string Id, long Depth, JsonObject Pdu)> chain = [];
        List<string> next = [.. state.SelectMany(e => AuthEvents(e.Pdu))];
        while (next.Count > 0)
        {
            List<(string Id, long Depth, JsonObject Pdu)> batch = await LoadAsync(db, roomId, [.. next.Where(seen.Add)], cancellationToken);
            chain.AddRange(batch);
            next = [.. batch.SelectMany(e => AuthEvents(e.Pdu))];
        }
        // Return both forms
        return ([.. state.Select(e => e.Pdu)], [.. chain.Select(e => e.Pdu)], [.. state.Select(e => e.Id)], [.. chain.Select(e => e.Id)]);
    }

    /// <summary>Assert that <paramref name="origin"/> has a member joined to the room, so it may see its history, and that the room's server ACL allows it.</summary>
    private static async Task AssertMemberServerAsync(ServerDbContext db, RoomAcls acls, string origin, string roomId, CancellationToken cancellationToken)
    {
        // Allowed by the ACL
        await acls.AssertAllowedAsync(origin, roomId, cancellationToken);

        // Joined members of the origin
        List<string> members = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).ToListAsync(cancellationToken);
        if (!members.Any(m => UserId.TryParse(m, out UserId user) && user.ServerName == origin)) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "The asking server has no member in the room.");
    }

    /// <summary>Return the stored PDUs of <paramref name="eventIds"/> in a room, with their IDs and depths.</summary>
    private static async Task<List<(string Id, long Depth, JsonObject Pdu)>> LoadAsync(ServerDbContext db, string roomId, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        // Rows of the room only
        if (eventIds.Count == 0) return [];
        var rows = await db.Events.AsNoTracking().Where(e => e.RoomId == roomId && eventIds.Contains(e.EventId)).Select(e => new { e.EventId, e.Depth, e.Json }).ToListAsync(cancellationToken);
        // Return parsed
        return [.. rows.Select(r => (r.EventId, r.Depth, JsonNode.Parse(r.Json)!.AsObject()))];
    }

    /// <summary>Return the <c>prev_events</c> of a PDU.</summary>
    private static IEnumerable<string> PrevEvents(JsonObject pdu) => (pdu["prev_events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>();

    /// <summary>Return the <c>auth_events</c> of a PDU.</summary>
    private static IEnumerable<string> AuthEvents(JsonObject pdu) => (pdu["auth_events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>();

    /// <summary>Return an <c>M_NOT_FOUND</c> error for an unknown event.</summary>
    private static MatrixException NotFound() => new(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown event.");
}
