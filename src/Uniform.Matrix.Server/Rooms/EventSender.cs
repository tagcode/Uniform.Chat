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
        // A retried transaction returns the event it already created; transaction IDs are per room, as the room is part of the endpoint
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (draft.DeviceId is string device && draft.TransactionId is string txn)
        {
            string? sent = await db.Transactions.AsNoTracking().Where(t => t.UserId == draft.Sender && t.DeviceId == device && t.TransactionId == txn)
                .Join(db.Events.Where(e => e.RoomId == draft.RoomId), t => t.EventId, e => e.EventId, (t, _) => t.EventId).FirstOrDefaultAsync(cancellationToken);
            if (sent is not null) return sent;
        }

        // The PDU on the current state, hashed and signed
        (RoomRow room, RoomVersion version, JsonObject pdu, Dictionary<(string, string), JsonObject> authState) = await BuildAsync(db, draft, cancellationToken);
        string serverName = options.Value.ServerName;
        EventFormat.HashAndSign(pdu, version, serverName, keys.KeyId, keys.Secret);
        string eventId = EventFormat.ComputeEventId(pdu, version);

        // Authorization against the current state
        string? rejection = AuthRules.Check(pdu, version, authState, (e, server) => server == serverName && EventFormat.HasValidSignature(e, version, serverName, keys.KeyId, keys.PublicKey));
        if (rejection is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, rejection);

        // Store with the transaction ID of the device
        await StoreAsync(db, room, version, pdu, eventId, position, draft.DeviceId is string deviceId && draft.TransactionId is string transactionId ? new TransactionRow { UserId = draft.Sender, DeviceId = deviceId, TransactionId = transactionId, EventId = eventId } : null, cancellationToken);
        // Return ID
        return eventId;
    }

    /// <summary>Build, hash and sign a local event and check it against the room's rules without storing it, for events another server must sign too, such as invites of its users; store it with <see cref="AcceptEventAsync"/>.</summary>
    public async Task<(JsonObject Pdu, string EventId, RoomVersion Version)> SignAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        // Built on the current state, like a sent event
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        (_, RoomVersion version, JsonObject pdu, Dictionary<(string, string), JsonObject> authState) = await BuildAsync(db, draft, cancellationToken);
        string serverName = options.Value.ServerName;
        EventFormat.HashAndSign(pdu, version, serverName, keys.KeyId, keys.Secret);

        // Assert the rules allow it now
        string? rejection = AuthRules.Check(pdu, version, authState, (e, server) => server == serverName && EventFormat.HasValidSignature(e, version, serverName, keys.KeyId, keys.PublicKey));
        if (rejection is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, rejection);
        // Return signed event
        return (pdu, EventFormat.ComputeEventId(pdu, version), version);
    }

    /// <summary>Return this server's public keys by key ID, for <see cref="AcceptEventAsync"/> of its own events.</summary>
    public IReadOnlyDictionary<string, string> OwnKeys => new Dictionary<string, string> { [keys.KeyId] = keys.PublicKey };

    /// <summary>Return the state an invitee sees before joining, as stripped events: create, join rules, name, avatar, alias, encryption, and the inviter's membership.</summary>
    public async Task<JsonArray> InviteRoomStateAsync(string roomId, string inviter, CancellationToken cancellationToken)
    {
        // Current state of those keys
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        (string Type, string StateKey)[] wanted = [(EventTypes.RoomCreate, ""), ("m.room.join_rules", ""), (EventTypes.RoomName, ""), ("m.room.avatar", ""), (EventTypes.RoomCanonicalAlias, ""), ("m.room.encryption", ""), (EventTypes.RoomMember, inviter)];
        Dictionary<(string, string), JsonObject> state = await LoadCurrentAsync(db, roomId, wanted, cancellationToken);
        // Return stripped events
        return new JsonArray([.. state.Values.Select(e => (JsonNode)new JsonObject { ["type"] = e["type"]?.DeepClone(), ["state_key"] = e["state_key"]?.DeepClone(), ["sender"] = e["sender"]?.DeepClone(), ["content"] = e["content"]?.DeepClone() })]);
    }

    /// <summary>Create a room whose ID is its create event's hash, as from room version 12: build and sign the create event first, then store the room under the ID it gives.</summary>
    /// <param name="version">Room version with <see cref="RoomVersion.RoomIdIsCreateHash"/>.</param>
    /// <param name="creator">Local user creating the room.</param>
    /// <param name="content">Create event content, with <c>room_version</c>.</param>
    /// <param name="published">Whether the room is listed in the public directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<string> CreateHashedRoomAsync(RoomVersion version, string creator, JsonObject content, bool published, CancellationToken cancellationToken) => stream.WriteAsync(async position =>
    {
        // The create event, without a room ID
        JsonObject pdu = new()
        {
            ["auth_events"] = new JsonArray(),
            ["content"] = content.DeepClone(),
            ["depth"] = 1L,
            ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
            ["prev_events"] = new JsonArray(),
            ["sender"] = creator,
            ["state_key"] = "",
            ["type"] = EventTypes.RoomCreate,
        };
        string serverName = options.Value.ServerName;
        EventFormat.HashAndSign(pdu, version, serverName, keys.KeyId, keys.Secret);
        string eventId = EventFormat.ComputeEventId(pdu, version);
        string roomId = RoomVersion.RoomIdOf(eventId);
        string? rejection = AuthRules.Check(pdu, version, new Dictionary<(string, string), JsonObject>());
        if (rejection is not null) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, rejection);

        // Room under that ID, then its first event
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomRow room = new() { RoomId = roomId, Version = version.Id, Creator = creator, CreatedTs = pdu["origin_server_ts"]!.GetValue<long>(), Published = published };
        db.Rooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        await StoreAsync(db, room, version, pdu, eventId, position, null, cancellationToken);
        // Return room ID
        return roomId;
    }, cancellationToken);

    /// <summary>Return an unsigned membership event of <paramref name="userId"/> for another server to sign, as <c>make_join</c> and <c>make_leave</c> answer, with the room version.</summary>
    /// <param name="roomId">Room here.</param>
    /// <param name="userId">User of the asking server.</param>
    /// <param name="membership"><c>join</c>, or <c>leave</c> to reject an invite or leave.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<(JsonObject Template, RoomVersion Version)> MakeMembershipAsync(string roomId, string userId, string membership, CancellationToken cancellationToken)
    {
        // Built on the current state and extremities, unsigned
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        EventDraft draft = new() { RoomId = roomId, Sender = userId, Type = EventTypes.RoomMember, StateKey = userId, Content = new JsonObject { ["membership"] = membership } };
        (_, RoomVersion version, JsonObject pdu, Dictionary<(string, string), JsonObject> authState) = await BuildAsync(db, draft, cancellationToken);

        // Assert the rules would allow it, other than the signature
        string? rejection = AuthRules.Check(pdu, version, authState, (_, _) => true);
        if (rejection is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, rejection);
        // Return template
        return (pdu, version);
    }

    /// <summary>Accept a membership event that another server built from <see cref="MakeMembershipAsync"/> and signed; for a join return the state before it and its auth chain, as <c>send_join</c> answers, for a leave nothing.</summary>
    /// <param name="pdu">The signed event.</param>
    /// <param name="membership"><c>join</c> or <c>leave</c>, which the event must be.</param>
    /// <param name="eventId">Event ID from the URL, which must match the event.</param>
    /// <param name="origin">Server that sent it, which must be the joining user's.</param>
    /// <param name="originKeys">Public keys of <paramref name="origin"/> by key ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<(List<JsonObject> State, List<JsonObject> AuthChain)> AcceptMembershipAsync(JsonObject pdu, string membership, string eventId, string origin, IReadOnlyDictionary<string, string> originKeys, CancellationToken cancellationToken) =>
        stream.WriteAsync(position => AcceptMembershipAtAsync(pdu, membership, eventId, origin, originKeys, position, cancellationToken), cancellationToken);

    /// <summary>Check and store a remote join or leave at stream <paramref name="position"/>.</summary>
    private async Task<(List<JsonObject> State, List<JsonObject> AuthChain)> AcceptMembershipAtAsync(JsonObject pdu, string membership, string eventId, string origin, IReadOnlyDictionary<string, string> originKeys, long position, CancellationToken cancellationToken)
    {
        // Assert the membership of a user of the origin, in a room here, with the ID it claims
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string? roomId = pdu["room_id"]?.GetValue<string>();
        string? sender = pdu["sender"]?.GetValue<string>();
        RoomRow room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        RoomVersion version = RoomVersion.Find(room.Version)!;
        if (pdu["type"]?.GetValue<string>() != EventTypes.RoomMember || pdu["state_key"]?.GetValue<string>() != sender || pdu["content"]?["membership"]?.GetValue<string>() != membership) throw Invalid($"The event is not a {membership}.");
        if (!UserId.TryParse(sender, out UserId user) || user.ServerName != origin) throw Invalid("The user is not of the sending server.");
        if (EventFormat.ComputeEventId(pdu, version) != eventId) throw Invalid("The event ID does not match the event.");
        if (!EventFormat.HasValidContentHash(pdu)) throw Invalid("The content hash does not match.");

        // Authorization against the current state, signed by the origin
        IReadOnlyList<(string Type, string StateKey)> needed = AuthEvents.Select(EventTypes.RoomMember, sender!, sender!, pdu["content"]!.AsObject());
        Dictionary<(string, string), JsonObject> authState = await LoadCurrentAsync(db, roomId!, needed, cancellationToken);
        string? rejection = AuthRules.Check(pdu, version, authState, (e, server) => server == origin && originKeys.Any(k => EventFormat.HasValidSignature(e, version, origin, k.Key, k.Value)));
        if (rejection is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, rejection);

        // For a join, the state before it and the auth events behind it
        List<JsonObject> state = membership == Memberships.Join ? await LoadEventsAsync(db, [.. (await StateGroups.LoadAsync(db, room.CurrentStateGroup, cancellationToken)).Values], cancellationToken) : [];
        List<JsonObject> authChain = membership == Memberships.Join ? await LoadAuthChainAsync(db, state, cancellationToken) : [];
        pdu.Remove("unsigned");
        await StoreAsync(db, room, version, pdu, eventId, position, null, cancellationToken);
        // Return state and chain
        return (state, authChain);
    }

    /// <summary>Accept an event another server sent in a transaction; return its ID, if known, and null when stored or already here, else why it was rejected.</summary>
    /// <param name="pdu">The event.</param>
    /// <param name="senderKeys">Public keys of the sender's server by key ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>The event is checked against the current state rather than the state before it, which holds while rooms have no forks.</remarks>
    public Task<(string? EventId, string? Error)> AcceptEventAsync(JsonObject pdu, IReadOnlyDictionary<string, string> senderKeys, CancellationToken cancellationToken) =>
        stream.WriteAsync(position => AcceptEventAtAsync(pdu, senderKeys, position, cancellationToken), cancellationToken);

    /// <summary>Check and store a received event at stream <paramref name="position"/>.</summary>
    private async Task<(string? EventId, string? Error)> AcceptEventAtAsync(JsonObject pdu, IReadOnlyDictionary<string, string> senderKeys, long position, CancellationToken cancellationToken)
    {
        // Assert a room here, a new event, and an intact one
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string? roomId = pdu["room_id"]?.GetValue<string>();
        RoomRow? room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken);
        if (room is null || RoomVersion.Find(room.Version) is not RoomVersion version) return (null, "Unknown room.");
        pdu.Remove("unsigned");
        string eventId = EventFormat.ComputeEventId(pdu, version);
        if (await db.Events.AnyAsync(e => e.EventId == eventId, cancellationToken)) return (eventId, null);
        if (!EventFormat.HasValidContentHash(pdu)) return (eventId, "The content hash does not match.");
        if (pdu["type"]?.GetValue<string>() is not string type || !UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId sender)) return (eventId, "The event has no type or sender.");

        // Authorization against the current state, signed by the sender's server
        string? stateKey = pdu["state_key"]?.GetValue<string>();
        IReadOnlyList<(string Type, string StateKey)> needed = AuthEvents.Select(type, stateKey, sender.ToString(), pdu["content"] as JsonObject ?? []);
        Dictionary<(string, string), JsonObject> authState = await LoadCurrentAsync(db, room.RoomId, needed, cancellationToken);
        string? rejection = AuthRules.Check(pdu, version, authState, (e, server) => server == sender.ServerName && senderKeys.Any(k => EventFormat.HasValidSignature(e, version, server, k.Key, k.Value)));
        if (rejection is not null) return (eventId, rejection);
        await StoreAsync(db, room, version, pdu, eventId, position, null, cancellationToken);
        // Return stored
        return (eventId, null);
    }

    /// <summary>Store a room joined on another server: its state and auth chain as they were before the join, then the join as the room's first own event.</summary>
    /// <param name="version">Room version.</param>
    /// <param name="join">The signed join, already accepted by the resident server.</param>
    /// <param name="joinId">Its event ID.</param>
    /// <param name="events">State and auth chain events by ID, already checked.</param>
    /// <param name="stateIds">IDs of the state events before the join.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task StoreJoinedRoomAsync(RoomVersion version, JsonObject join, string joinId, IReadOnlyDictionary<string, JsonObject> events, IReadOnlyCollection<string> stateIds, CancellationToken cancellationToken) =>
        stream.WriteAsync(async position =>
        {
            // Assert the room is new here, with a create event
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            string roomId = join["room_id"]!.GetValue<string>();
            if (await db.Rooms.AnyAsync(r => r.RoomId == roomId, cancellationToken)) throw new MatrixException(HttpStatusCode.Conflict, ErrorCodes.Unknown, "The room is already here.");
            JsonObject create = stateIds.Select(id => events[id]).FirstOrDefault(e => e["type"]?.GetValue<string>() == EventTypes.RoomCreate) ?? throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "The room's state has no create event.");
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Earlier events sort before every stream position, so they are history and not news
            long earlier = -position * 1_000_000;
            foreach ((string eventId, JsonObject pdu) in events)
            {
                db.Events.Add(new EventRow { EventId = eventId, RoomId = roomId, Stream = --earlier, Depth = pdu["depth"]?.GetValue<long>() ?? 0, Type = pdu["type"]!.GetValue<string>(), StateKey = pdu["state_key"]?.GetValue<string>(), Sender = pdu["sender"]!.GetValue<string>(), OriginServerTs = pdu["origin_server_ts"]?.GetValue<long>() ?? 0, Json = pdu.ToJsonString() });
            }

            // State before the join as the room's first group and current state
            List<(string Type, string StateKey, string EventId)> state = [.. stateIds.Select(id => (events[id]["type"]!.GetValue<string>(), events[id]["state_key"]!.GetValue<string>(), id))];
            RoomRow room = new() { RoomId = roomId, Version = version.Id, Creator = create["sender"]!.GetValue<string>(), CreatedTs = create["origin_server_ts"]?.GetValue<long>() ?? 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync(cancellationToken);
            room.CurrentStateGroup = await StateGroups.CreateAsync(db, roomId, 0, state, cancellationToken);
            foreach ((string type, string stateKey, string eventId) in state) await UpdateCurrentStateAsync(db, roomId, type, stateKey, type == EventTypes.RoomMember ? events[eventId]["content"]?["membership"]?.GetValue<string>() : null, eventId, position, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            // The join on top, then all at once
            await StoreAsync(db, room, version, join, joinId, position, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Return the PDUs of <paramref name="eventIds"/> as stored, without their IDs, which are not part of the room version's format.</summary>
    private static async Task<List<JsonObject>> LoadEventsAsync(ServerDbContext dbContext, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken) =>
        [.. (await dbContext.Events.AsNoTracking().Where(e => eventIds.Contains(e.EventId)).Select(e => e.Json).ToListAsync(cancellationToken)).Select(json => JsonNode.Parse(json)!.AsObject())];

    /// <summary>Return every auth event reachable from <paramref name="events"/>.</summary>
    private static async Task<List<JsonObject>> LoadAuthChainAsync(ServerDbContext dbContext, IEnumerable<JsonObject> events, CancellationToken cancellationToken)
    {
        // Breadth first over auth_events
        HashSet<string> seen = [];
        List<JsonObject> chain = [];
        List<string> next = [.. events.SelectMany(AuthIds)];
        while (next.Count > 0)
        {
            List<string> batch = [.. next.Where(seen.Add)];
            List<JsonObject> loaded = await LoadEventsAsync(dbContext, batch, cancellationToken);
            chain.AddRange(loaded);
            next = [.. loaded.SelectMany(AuthIds).Where(id => !seen.Contains(id))];
        }
        // Return chain
        return chain;
    }

    /// <summary>Return the auth event IDs of a PDU.</summary>
    private static IEnumerable<string> AuthIds(JsonObject pdu) => IdsOf(pdu, "auth_events");

    /// <summary>Return the room, its version, the unsigned PDU of <paramref name="draft"/> on the current extremities, and the auth state it was built on.</summary>
    private async Task<(RoomRow Room, RoomVersion Version, JsonObject Pdu, Dictionary<(string, string), JsonObject> AuthState)> BuildAsync(ServerDbContext db, EventDraft draft, CancellationToken cancellationToken)
    {
        // Room and version
        RoomRow room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == draft.RoomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        RoomVersion version = RoomVersion.Find(room.Version) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UnsupportedRoomVersion, $"Room version {room.Version} is not supported.");

        // Previous events and depth
        List<string> prevEvents = await db.ForwardExtremities.Where(e => e.RoomId == draft.RoomId).Select(e => e.EventId).OrderBy(e => e).ToListAsync(cancellationToken);
        long depth = prevEvents.Count == 0 ? 1 : await db.Events.Where(e => prevEvents.Contains(e.EventId)).MaxAsync(e => e.Depth, cancellationToken) + 1;

        // Auth events from the current state
        IReadOnlyList<(string Type, string StateKey)> needed = AuthEvents.Select(draft.Type, draft.StateKey, draft.Sender, draft.Content);
        Dictionary<(string, string), JsonObject> authState = await LoadCurrentAsync(db, draft.RoomId, needed, cancellationToken);

        // The PDU without hash and signature
        JsonObject pdu = new()
        {
            ["auth_events"] = new JsonArray([.. authState.Where(e => !version.RoomIdIsCreateHash || e.Key.Item1 != EventTypes.RoomCreate).Select(e => e.Value["event_id"]!.GetValue<string>()).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
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
        // Return parts
        return (room, version, pdu, authState);
    }

    /// <summary>Store an authorized event, its graph edges and state change in one transaction; it replaces the forward extremities it builds on.</summary>
    private static async Task StoreAsync(ServerDbContext db, RoomRow room, RoomVersion version, JsonObject pdu, string eventId, long position, TransactionRow? transactionRow, CancellationToken cancellationToken)
    {
        // Parts of the event
        string type = pdu["type"]!.GetValue<string>();
        string? stateKey = pdu["state_key"]?.GetValue<string>();
        string sender = pdu["sender"]!.GetValue<string>();
        JsonObject content = pdu["content"] as JsonObject ?? [];
        List<string> prevEvents = [.. IdsOf(pdu, "prev_events")];

        // State, event and edges, inside the caller's transaction if there is one
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        long group = room.CurrentStateGroup;
        if (stateKey is not null)
        {
            group = await StateGroups.CreateAsync(db, room.RoomId, room.CurrentStateGroup, [(type, stateKey, eventId)], cancellationToken);
            room.CurrentStateGroup = group;
            await UpdateCurrentStateAsync(db, room.RoomId, type, stateKey, type == EventTypes.RoomMember ? content["membership"]?.GetValue<string>() : null, eventId, position, cancellationToken);
        }
        db.Events.Add(new EventRow { EventId = eventId, RoomId = room.RoomId, Stream = position, Depth = pdu["depth"]!.GetValue<long>(), Type = type, StateKey = stateKey, Sender = sender, OriginServerTs = pdu["origin_server_ts"]!.GetValue<long>(), Json = pdu.ToJsonString(), StateGroup = group });
        foreach (string prev in prevEvents) db.EventEdges.Add(new EventEdgeRow { EventId = eventId, PrevEventId = prev });
        await db.ForwardExtremities.Where(e => e.RoomId == room.RoomId && prevEvents.Contains(e.EventId)).ExecuteDeleteAsync(cancellationToken);
        db.ForwardExtremities.Add(new ForwardExtremityRow { RoomId = room.RoomId, EventId = eventId });
        if (transactionRow is not null) db.Transactions.Add(transactionRow);
        if (type == EventTypes.RoomRedaction) await ApplyRedactionAsync(db, version, room.RoomId, pdu["redacts"]?.GetValue<string>() ?? content["redacts"]?.GetValue<string>(), eventId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // Done
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Return the event IDs in an array field of a PDU.</summary>
    private static IEnumerable<string> IdsOf(JsonObject pdu, string field) => (pdu[field] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>();

    /// <summary>Return an <c>M_INVALID_PARAM</c> error about a received event.</summary>
    private static MatrixException Invalid(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, message);

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
    internal static async Task UpdateCurrentStateAsync(ServerDbContext dbContext, string roomId, string type, string stateKey, string? membership, string eventId, long stream, CancellationToken cancellationToken)
    {
        // Upsert
        CurrentStateRow? row = await dbContext.CurrentState.SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == type && s.StateKey == stateKey, cancellationToken);
        if (row is null) dbContext.CurrentState.Add(row = new CurrentStateRow { RoomId = roomId, Type = type, StateKey = stateKey, EventId = eventId });
        row.EventId = eventId;
        row.Stream = stream;
        row.Membership = membership;
    }

    /// <summary>Replace the redacted event's stored form with its redaction.</summary>
    private static async Task ApplyRedactionAsync(ServerDbContext dbContext, RoomVersion version, string roomId, string? target, string redactionId, CancellationToken cancellationToken)
    {
        // Target in the same room
        EventRow? row = target is null ? null : await dbContext.Events.SingleOrDefaultAsync(e => e.EventId == target && e.RoomId == roomId, cancellationToken);
        if (row is null || row.RedactedBy is not null) return;
        // Keep only the protected keys
        row.Json = EventFormat.Redact(JsonNode.Parse(row.Json)!.AsObject(), version).ToJsonString();
        row.RedactedBy = redactionId;
    }
}
