namespace Uniform.Matrix.Server.Rooms;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>An event a local user wants to send, before the server builds it into the room graph.</summary>
public sealed record EventDraft
{
    /// <summary>Room.</summary>
    public required string RoomId { get; init; }
    /// <summary>Sender.</summary>
    public required string Sender { get; init; }
    /// <summary>Event type.</summary>
    public required string Type { get; init; }
    /// <summary>State key for state events; null for others.</summary>
    public string? StateKey { get; init; }
    /// <summary>Event content.</summary>
    public required JsonObject Content { get; init; }
    /// <summary>Event redacted by an <c>m.room.redaction</c>, set at the top level in room versions before 11.</summary>
    public string? Redacts { get; init; }
    /// <summary>Sending device, with <see cref="TransactionId"/> to deduplicate retries.</summary>
    public string? DeviceId { get; init; }
    /// <summary>Client transaction ID.</summary>
    public string? TransactionId { get; init; }
}

/// <summary>Builds local events into the room graph: previous events, auth events, hash, signature, ID, authorization, state and storage.</summary>
/// <remarks>Events are written through the <see cref="ServerStream"/>, one at a time, so stream positions are committed in order.</remarks>
public sealed class EventSender
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerKeys keys;
    private readonly IOptions<ServerOptions> options;
    private readonly TimeProvider time;
    private readonly ServerStream stream;

    /// <summary>Create the sender.</summary>
    public EventSender(IDbContextFactory<ServerDbContext> contexts, ServerKeys keys, IOptions<ServerOptions> options, TimeProvider time, ServerStream stream)
    {
        // Store dependencies
        this.contexts = contexts;
        this.keys = keys;
        this.options = options;
        this.time = time;
        this.stream = stream;
    }

    /// <summary>Send an event; return its ID. Throws <c>M_FORBIDDEN</c> when the auth rules reject it, and return the earlier event for a retried transaction.</summary>
    public Task<string> SendAsync(EventDraft draft, CancellationToken cancellationToken) => stream.WriteAsync(position => SendAtAsync(draft, position, cancellationToken), cancellationToken);

    /// <summary>Build and store the event at stream <paramref name="position"/>.</summary>
    private async Task<string> SendAtAsync(EventDraft draft, long position, CancellationToken cancellationToken)
    {
        // A retried transaction returns the event it already created
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (draft.DeviceId is string device && draft.TransactionId is string txn)
        {
            TransactionRow? sent = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.UserId == draft.Sender && t.DeviceId == device && t.TransactionId == txn, cancellationToken);
            if (sent is not null) return sent.EventId;
        }
        RoomRow room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == draft.RoomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        RoomVersion version = RoomVersion.Find(room.Version) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UnsupportedRoomVersion, $"Room version {room.Version} is not supported.");

        // Previous events and depth
        List<string> prevEvents = await db.ForwardExtremities.Where(e => e.RoomId == draft.RoomId).Select(e => e.EventId).OrderBy(e => e).ToListAsync(cancellationToken);
        long depth = prevEvents.Count == 0 ? 1 : await db.Events.Where(e => prevEvents.Contains(e.EventId)).MaxAsync(e => e.Depth, cancellationToken) + 1;

        // Auth events from the current state
        IReadOnlyList<(string Type, string StateKey)> needed = AuthEvents.Select(draft.Type, draft.StateKey, draft.Sender, draft.Content);
        Dictionary<(string, string), JsonObject> authState = await LoadCurrentAsync(db, draft.RoomId, needed, cancellationToken);

        // The PDU, hashed and signed
        JsonObject pdu = new()
        {
            ["auth_events"] = new JsonArray([.. authState.Values.Select(e => e["event_id"]!.GetValue<string>()).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
            ["content"] = draft.Content.DeepClone(),
            ["depth"] = depth,
            ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
            ["prev_events"] = new JsonArray([.. prevEvents.Select(e => (JsonNode)e)]),
            ["room_id"] = draft.RoomId,
            ["sender"] = draft.Sender,
            ["type"] = draft.Type,
        };
        if (draft.StateKey is not null) pdu["state_key"] = draft.StateKey;
        if (draft.Redacts is not null && version.Redaction == RedactionRules.V10) pdu["redacts"] = draft.Redacts;
        string serverName = options.Value.ServerName;
        EventFormat.HashAndSign(pdu, version, serverName, keys.KeyId, keys.Secret);
        string eventId = EventFormat.ComputeEventId(pdu, version);

        // Authorization against the current state
        string? rejection = AuthRules.Check(pdu, version, authState, (e, server) => server == serverName && EventFormat.HasValidSignature(e, version, serverName, keys.KeyId, keys.PublicKey));
        if (rejection is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, rejection);

        // Store event, graph edges and state in one transaction
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        long group = room.CurrentStateGroup;
        if (draft.StateKey is not null)
        {
            group = await StateGroups.CreateAsync(db, draft.RoomId, room.CurrentStateGroup, [(draft.Type, draft.StateKey, eventId)], cancellationToken);
            room.CurrentStateGroup = group;
            await UpdateCurrentStateAsync(db, draft, eventId, position, cancellationToken);
        }
        db.Events.Add(new EventRow { EventId = eventId, RoomId = draft.RoomId, Stream = position, Depth = depth, Type = draft.Type, StateKey = draft.StateKey, Sender = draft.Sender, OriginServerTs = pdu["origin_server_ts"]!.GetValue<long>(), Json = pdu.ToJsonString(), StateGroup = group });
        foreach (string prev in prevEvents) db.EventEdges.Add(new EventEdgeRow { EventId = eventId, PrevEventId = prev });
        await db.ForwardExtremities.Where(e => e.RoomId == draft.RoomId).ExecuteDeleteAsync(cancellationToken);
        db.ForwardExtremities.Add(new ForwardExtremityRow { RoomId = draft.RoomId, EventId = eventId });
        if (draft.DeviceId is string deviceId && draft.TransactionId is string transactionId) db.Transactions.Add(new TransactionRow { UserId = draft.Sender, DeviceId = deviceId, TransactionId = transactionId, EventId = eventId });
        if (draft.Type == EventTypes.RoomRedaction) await ApplyRedactionAsync(db, version, draft, eventId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Return ID
        return eventId;
    }

    /// <summary>Return the current state events of <paramref name="needed"/>, with their IDs added.</summary>
    private static async Task<Dictionary<(string, string), JsonObject>> LoadCurrentAsync(ServerDbContext dbContext, string roomId, IReadOnlyList<(string Type, string StateKey)> needed, CancellationToken cancellationToken)
    {
        // Current state rows of the room for the needed types
        List<string> types = [.. needed.Select(n => n.Type).Distinct()];
        List<CurrentStateRow> rows = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && types.Contains(s.Type)).ToListAsync(cancellationToken);
        List<string> ids = [.. rows.Where(r => needed.Contains((r.Type, r.StateKey))).Select(r => r.EventId)];
        Dictionary<string, string> json = await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).ToDictionaryAsync(e => e.EventId, e => e.Json, cancellationToken);

        // Events with IDs, as the auth rules expect
        Dictionary<(string, string), JsonObject> state = [];
        foreach (CurrentStateRow row in rows.Where(r => ids.Contains(r.EventId)))
        {
            JsonObject pdu = JsonNode.Parse(json[row.EventId])!.AsObject();
            pdu["event_id"] = row.EventId;
            state[(row.Type, row.StateKey)] = pdu;
        }
        // Return state
        return state;
    }

    /// <summary>Set the current state entry of a state event.</summary>
    private static async Task UpdateCurrentStateAsync(ServerDbContext dbContext, EventDraft draft, string eventId, long stream, CancellationToken cancellationToken)
    {
        // Upsert
        CurrentStateRow? row = await dbContext.CurrentState.SingleOrDefaultAsync(s => s.RoomId == draft.RoomId && s.Type == draft.Type && s.StateKey == draft.StateKey, cancellationToken);
        if (row is null) dbContext.CurrentState.Add(row = new CurrentStateRow { RoomId = draft.RoomId, Type = draft.Type, StateKey = draft.StateKey!, EventId = eventId });
        row.EventId = eventId;
        row.Stream = stream;
        row.Membership = draft.Type == EventTypes.RoomMember ? draft.Content["membership"]?.GetValue<string>() : null;
    }

    /// <summary>Replace the redacted event's stored form with its redaction.</summary>
    private static async Task ApplyRedactionAsync(ServerDbContext dbContext, RoomVersion version, EventDraft draft, string redactionId, CancellationToken cancellationToken)
    {
        // Target in the same room
        string? target = draft.Redacts ?? draft.Content["redacts"]?.GetValue<string>();
        EventRow? row = target is null ? null : await dbContext.Events.SingleOrDefaultAsync(e => e.EventId == target && e.RoomId == draft.RoomId, cancellationToken);
        if (row is null || row.RedactedBy is not null) return;
        // Keep only the protected keys
        row.Json = EventFormat.Redact(JsonNode.Parse(row.Json)!.AsObject(), version).ToJsonString();
        row.RedactedBy = redactionId;
    }
}
