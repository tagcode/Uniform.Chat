namespace Uniform.Matrix.Server.Rooms;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Storage;

// Events other servers send: the checks of the spec's receiving steps, state before and after each event, soft failure, and the current state merged over forks.
public sealed partial class EventSender
{
    /// <summary>Most events loaded per query, below SQLite's limit on parameters.</summary>
    private const int LoadBatch = 500;

    /// <summary>Accept an event another server sent; return its ID, if known, and null when stored or already here, else why it was rejected.</summary>
    /// <param name="pdu">The event.</param>
    /// <param name="senderKeys">Public keys of the sender's server by key ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="reportedStateBefore">State before the event as its server reported it, for previous events not here; null when all are here.</param>
    /// <remarks>
    /// As the spec's <see href="https://spec.matrix.org/latest/server-server-api/#checks-performed-on-receipt-of-a-pdu">checks on receipt</see>:
    /// the event must be allowed by its own auth events, which must be here, and by the state before it, merged from its previous events; otherwise it is rejected and not stored.
    /// When the room's current state no longer allows it, as when its sender was banned meanwhile, it is stored but <see cref="EventRow.SoftFailed">soft-failed</see>.
    /// </remarks>
    public Task<(string? EventId, string? Error)> AcceptEventAsync(JsonObject pdu, IReadOnlyDictionary<string, string> senderKeys, CancellationToken cancellationToken, IReadOnlyDictionary<(string Type, string StateKey), string>? reportedStateBefore = null) =>
        stream.WriteAsync(position => AcceptEventAtAsync(pdu, senderKeys, reportedStateBefore, position, cancellationToken), cancellationToken);

    /// <summary>Return the IDs among <paramref name="eventIds"/> that are not stored here, soft-failed ones counting as stored.</summary>
    public async Task<IReadOnlyList<string>> FindUnknownAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        // Known ones in batches
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        HashSet<string> known = [];
        foreach (string[] batch in eventIds.Distinct().Chunk(LoadBatch)) known.UnionWith(await db.Events.IgnoreQueryFilters().Where(e => batch.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(cancellationToken));
        // Return the rest
        return [.. eventIds.Distinct().Where(id => !known.Contains(id))];
    }

    /// <summary>Store events of a room fetched to fill a gap, e.g. the state before an event and its auth chain, as earlier history; those not allowed by their own auth events are left out. Return the IDs stored.</summary>
    /// <param name="roomId">Room, which must be here.</param>
    /// <param name="events">Events by ID, with hashes and signatures already checked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlySet<string>> StoreFetchedAsync(string roomId, IReadOnlyDictionary<string, JsonObject> events, CancellationToken cancellationToken) =>
        stream.WriteAsync<IReadOnlySet<string>>(async position =>
        {
            // Assert the room
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            RoomRow? room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken);
            if (room is null || RoomVersion.Find(room.Version) is not RoomVersion version) return new HashSet<string>();

            // Allowed against their auth events, which may already be here, and the create event, which version 12 events do not list
            Dictionary<string, JsonObject> all = new(events);
            List<string> outside = [.. events.Values.SelectMany(AuthIds).Where(id => !events.ContainsKey(id)).Distinct()];
            foreach (JsonObject known in await LoadWithIdsAsync(db, outside, cancellationToken)) all.TryAdd(known["event_id"]!.GetValue<string>(), Without(known, "event_id"));
            foreach (JsonObject create in (await LoadCurrentAsync(db, roomId, [(EventTypes.RoomCreate, "")], cancellationToken)).Values) all.TryAdd(create["event_id"]!.GetValue<string>(), Without(create, "event_id"));
            HashSet<string> allowed = AuthRules.CheckAuthChains(version, all);
            List<string> ids = [.. events.Keys.Where(allowed.Contains)];
            IReadOnlyList<string> unknown = await FindUnknownAsync(ids, cancellationToken);

            // Earlier history, sorting before every stream position
            long earlier = -position * 1_000_000;
            foreach (string eventId in unknown)
            {
                JsonObject pdu = events[eventId];
                db.Events.Add(new EventRow { EventId = eventId, RoomId = roomId, Stream = --earlier, Depth = pdu["depth"]?.GetValue<long>() ?? 0, Type = pdu["type"]!.GetValue<string>(), StateKey = pdu["state_key"]?.GetValue<string>(), Sender = pdu["sender"]!.GetValue<string>(), OriginServerTs = pdu["origin_server_ts"]?.GetValue<long>() ?? 0, Json = pdu.ToJsonString() });
                AddRelation(db, eventId, pdu);
            }
            await db.SaveChangesAsync(cancellationToken);
            // Return stored
            return new HashSet<string>(unknown);
        }, cancellationToken);

    /// <summary>Check and store a received event at stream <paramref name="position"/>.</summary>
    private async Task<(string? EventId, string? Error)> AcceptEventAtAsync(JsonObject pdu, IReadOnlyDictionary<string, string> senderKeys, IReadOnlyDictionary<(string Type, string StateKey), string>? reportedStateBefore, long position, CancellationToken cancellationToken)
    {
        // Assert a room here, a new event, and an intact one
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string? roomId = pdu["room_id"]?.GetValue<string>();
        RoomRow? room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken);
        if (room is null || RoomVersion.Find(room.Version) is not RoomVersion version) return (null, "Unknown room.");
        pdu.Remove("unsigned");
        string eventId = EventFormat.ComputeEventId(pdu, version);
        if (await db.Events.IgnoreQueryFilters().AnyAsync(e => e.EventId == eventId, cancellationToken)) return (eventId, null);
        if (!EventFormat.HasValidContentHash(pdu)) return (eventId, "The content hash does not match.");
        if (pdu["type"]?.GetValue<string>() is not string type || !UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId sender)) return (eventId, "The event has no type or sender.");
        string? stateKey = pdu["state_key"]?.GetValue<string>();
        IReadOnlyList<(string Type, string StateKey)> needed = AuthEvents.Select(type, stateKey, sender.ToString(), pdu["content"] as JsonObject ?? []);
        Func<JsonObject, string, bool> signedBySender = (e, server) => server == sender.ServerName && senderKeys.Any(k => EventFormat.HasValidSignature(e, version, server, k.Key, k.Value));

        // Allowed by its own auth events, which must be here, of this room, and the ones the rules select
        if (await LoadAuthEventsAsync(db, room, version, pdu, needed, cancellationToken) is not Dictionary<(string, string), JsonObject> authEvents) return (eventId, "Its auth events are unknown here or not the ones its type needs.");
        if (AuthRules.Check(pdu, version, authEvents, signedBySender) is string invalid) return (eventId, invalid);

        // Allowed by the state before it
        Dictionary<(string Type, string StateKey), string> stateBefore = await StateBeforeAsync(db, room, version, pdu, reportedStateBefore, cancellationToken);
        if (AuthRules.Check(pdu, version, await LoadStateEventsAsync(db, stateBefore, needed, cancellationToken), signedBySender) is string rejection) return (eventId, rejection);

        // Soft-failed when the current state no longer allows it
        bool softFailed = AuthRules.Check(pdu, version, await LoadCurrentAsync(db, room.RoomId, needed, cancellationToken), signedBySender) is not null;
        await StoreRemoteAsync(db, room, version, pdu, eventId, stateBefore, softFailed, position, cancellationToken);
        // Return stored
        return (eventId, null);
    }

    /// <summary>Return an event's auth events by type and state key, with the create event in version 12, which names it by room ID; null when one is unknown, of another room, repeated, or not of the <paramref name="needed"/> keys.</summary>
    private static async Task<Dictionary<(string, string), JsonObject>?> LoadAuthEventsAsync(ServerDbContext db, RoomRow room, RoomVersion version, JsonObject pdu, IReadOnlyList<(string Type, string StateKey)> needed, CancellationToken cancellationToken)
    {
        // All known and of this room
        List<string> ids = [.. AuthIds(pdu)];
        List<JsonObject> loaded = [.. (await LoadWithIdsAsync(db, ids, cancellationToken)).Where(e => e["room_id"]?.GetValue<string>() == room.RoomId || e["type"]?.GetValue<string>() == EventTypes.RoomCreate)];
        if (loaded.Count != ids.Distinct().Count()) return null;

        // Keyed once each, by needed keys only
        Dictionary<(string, string), JsonObject> state = [];
        foreach (JsonObject authEvent in loaded)
        {
            (string, string) key = (authEvent["type"]?.GetValue<string>() ?? "", authEvent["state_key"]?.GetValue<string>() ?? "");
            if (authEvent["state_key"] is null || !needed.Contains(key) || !state.TryAdd(key, authEvent)) return null;
        }
        if (version.RoomIdIsCreateHash && !state.ContainsKey((EventTypes.RoomCreate, "")))
        {
            Dictionary<(string, string), JsonObject> create = await LoadCurrentAsync(db, room.RoomId, [(EventTypes.RoomCreate, "")], cancellationToken);
            foreach (((string, string) key, JsonObject createEvent) in create) state[key] = createEvent;
        }
        // Return auth state
        return state;
    }

    /// <summary>Return the state before <paramref name="pdu"/>: the state after each of its previous events known here, plus the reported state for those not here, merged when they differ; the current state when none is known.</summary>
    private static async Task<Dictionary<(string Type, string StateKey), string>> StateBeforeAsync(ServerDbContext db, RoomRow room, RoomVersion version, JsonObject pdu, IReadOnlyDictionary<(string Type, string StateKey), string>? reported, CancellationToken cancellationToken)
    {
        // States after the previous events here; history from joins and gaps has none
        List<string> previous = [.. IdsOf(pdu, "prev_events")];
        List<long> groups = await db.Events.IgnoreQueryFilters().AsNoTracking().Where(e => previous.Contains(e.EventId) && e.RoomId == room.RoomId && e.StateGroup != 0).Select(e => e.StateGroup).Distinct().ToListAsync(cancellationToken);
        List<IReadOnlyDictionary<(string Type, string StateKey), string>> sets = [];
        foreach (long group in groups) sets.Add(await StateGroups.LoadAsync(db, group, cancellationToken));
        int knownPrevious = await db.Events.IgnoreQueryFilters().CountAsync(e => previous.Contains(e.EventId) && e.RoomId == room.RoomId && e.StateGroup != 0, cancellationToken);
        if (knownPrevious < previous.Count && reported is not null) sets.Add(reported);

        // Return the one state, or the merge of several
        if (sets.Count == 0) return await StateGroups.LoadAsync(db, room.CurrentStateGroup, cancellationToken);
        if (sets.Skip(1).All(s => SameState(s, sets[0]))) return new(sets[0]);
        return await ResolveAsync(db, version, sets, cancellationToken);
    }

    /// <summary>Return the state events of <paramref name="needed"/> in <paramref name="state"/>, with their IDs added, as the auth rules expect.</summary>
    private static async Task<Dictionary<(string, string), JsonObject>> LoadStateEventsAsync(ServerDbContext db, IReadOnlyDictionary<(string Type, string StateKey), string> state, IReadOnlyList<(string Type, string StateKey)> needed, CancellationToken cancellationToken)
    {
        // Needed keys present in the state
        Dictionary<string, (string, string)> keys = needed.Where(state.ContainsKey).Distinct().ToDictionary(k => state[k], k => k);
        // Return events by key
        return (await LoadWithIdsAsync(db, keys.Keys, cancellationToken)).ToDictionary(e => keys[e["event_id"]!.GetValue<string>()], e => e);
    }

    /// <summary>Return the merge of several states by <see cref="StateResolution"/>, loading their events and auth chains.</summary>
    private static async Task<Dictionary<(string Type, string StateKey), string>> ResolveAsync(ServerDbContext db, RoomVersion version, IReadOnlyList<IReadOnlyDictionary<(string Type, string StateKey), string>> sets, CancellationToken cancellationToken)
    {
        // The states' events and everything they were authorized by
        Dictionary<string, JsonObject> events = [];
        List<string> next = [.. sets.SelectMany(s => s.Values).Distinct()];
        while (next.Count > 0)
        {
            List<JsonObject> loaded = await LoadWithIdsAsync(db, next, cancellationToken);
            foreach (JsonObject pdu in loaded) events[pdu["event_id"]!.GetValue<string>()] = Without(pdu, "event_id");
            next = [.. loaded.SelectMany(AuthIds).Distinct().Where(id => !events.ContainsKey(id))];
        }
        // Return merged state
        return StateResolution.Resolve(version, sets, events);
    }

    /// <summary>Store an accepted event with the state after it; unless soft-failed, it becomes a forward extremity and the room's current state follows, merged with other branches.</summary>
    private static async Task StoreRemoteAsync(ServerDbContext db, RoomRow room, RoomVersion version, JsonObject pdu, string eventId, Dictionary<(string Type, string StateKey), string> stateBefore, bool softFailed, long position, CancellationToken cancellationToken)
    {
        // Parts of the event
        string type = pdu["type"]!.GetValue<string>();
        string? stateKey = pdu["state_key"]?.GetValue<string>();
        List<string> previous = [.. IdsOf(pdu, "prev_events")];
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The event, the state after it and its edges
        Dictionary<(string Type, string StateKey), string> stateAfter = new(stateBefore);
        if (stateKey is not null) stateAfter[(type, stateKey)] = eventId;
        long groupBefore = await StateGroups.CreateFromStateAsync(db, room.RoomId, room.CurrentStateGroup, stateBefore, cancellationToken);
        long group = await StateGroups.CreateFromStateAsync(db, room.RoomId, groupBefore, stateAfter, cancellationToken);
        db.Events.Add(new EventRow { EventId = eventId, RoomId = room.RoomId, Stream = position, Depth = pdu["depth"]?.GetValue<long>() ?? 0, Type = type, StateKey = stateKey, Sender = pdu["sender"]!.GetValue<string>(), OriginServerTs = pdu["origin_server_ts"]?.GetValue<long>() ?? 0, Json = pdu.ToJsonString(), StateGroup = group, StateGroupBefore = groupBefore, SoftFailed = softFailed });
        AddRelation(db, eventId, pdu);
        foreach (string prev in previous) db.EventEdges.Add(new EventEdgeRow { EventId = eventId, PrevEventId = prev });
        if (softFailed)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // A forward extremity in place of the ones it builds on
        await db.ForwardExtremities.Where(e => e.RoomId == room.RoomId && previous.Contains(e.EventId)).ExecuteDeleteAsync(cancellationToken);
        db.ForwardExtremities.Add(new ForwardExtremityRow { RoomId = room.RoomId, EventId = eventId });
        await db.SaveChangesAsync(cancellationToken);

        // Current state: the event's, or merged with the other branches' latest states
        List<string> extremities = await db.ForwardExtremities.AsNoTracking().Where(e => e.RoomId == room.RoomId).Select(e => e.EventId).ToListAsync(cancellationToken);
        List<long> otherGroups = await db.Events.IgnoreQueryFilters().AsNoTracking().Where(e => extremities.Contains(e.EventId) && e.EventId != eventId && e.StateGroup != 0).Select(e => e.StateGroup).Distinct().ToListAsync(cancellationToken);
        long currentGroup = group;
        Dictionary<(string Type, string StateKey), string> current = stateAfter;
        if (otherGroups.Count > 0)
        {
            List<IReadOnlyDictionary<(string Type, string StateKey), string>> sets = [stateAfter];
            foreach (long other in otherGroups) sets.Add(await StateGroups.LoadAsync(db, other, cancellationToken));
            current = await ResolveAsync(db, version, sets, cancellationToken);
            currentGroup = await StateGroups.CreateFromStateAsync(db, room.RoomId, group, current, cancellationToken);
        }
        await ReplaceCurrentStateAsync(db, room, current, position, cancellationToken);
        room.CurrentStateGroup = currentGroup;

        // Redactions apply once accepted
        if (type == EventTypes.RoomRedaction) await ApplyRedactionAsync(db, version, room.RoomId, pdu["redacts"]?.GetValue<string>() ?? pdu["content"]?["redacts"]?.GetValue<string>(), eventId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // Done
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Make <paramref name="state"/> the room's current state rows: changed entries at <paramref name="position"/>, removed ones deleted.</summary>
    private static async Task ReplaceCurrentStateAsync(ServerDbContext db, RoomRow room, IReadOnlyDictionary<(string Type, string StateKey), string> state, long position, CancellationToken cancellationToken)
    {
        // Rows that differ
        List<CurrentStateRow> rows = await db.CurrentState.Where(s => s.RoomId == room.RoomId).ToListAsync(cancellationToken);
        Dictionary<(string, string), CurrentStateRow> byKey = rows.ToDictionary(r => (r.Type, r.StateKey));
        db.CurrentState.RemoveRange(rows.Where(r => !state.ContainsKey((r.Type, r.StateKey))));
        List<KeyValuePair<(string Type, string StateKey), string>> changed = [.. state.Where(e => byKey.GetValueOrDefault(e.Key)?.EventId != e.Value)];

        // Memberships of changed member events
        List<string> memberIds = [.. changed.Where(e => e.Key.Type == EventTypes.RoomMember).Select(e => e.Value)];
        Dictionary<string, string?> memberships = (await LoadWithIdsAsync(db, memberIds, cancellationToken)).ToDictionary(e => e["event_id"]!.GetValue<string>(), e => e["content"]?["membership"]?.GetValue<string>());
        foreach (((string type, string stateKey), string eventId) in changed)
        {
            CurrentStateRow row = byKey.GetValueOrDefault((type, stateKey)) ?? db.CurrentState.Add(new CurrentStateRow { RoomId = room.RoomId, Type = type, StateKey = stateKey, EventId = eventId }).Entity;
            row.EventId = eventId;
            row.Stream = position;
            row.Membership = type == EventTypes.RoomMember ? memberships.GetValueOrDefault(eventId) : null;
        }
        // Done
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return the stored events of <paramref name="eventIds"/>, soft-failed ones too, each with its <c>event_id</c> added.</summary>
    private static async Task<List<JsonObject>> LoadWithIdsAsync(ServerDbContext db, IEnumerable<string> eventIds, CancellationToken cancellationToken)
    {
        // In batches
        List<JsonObject> events = [];
        foreach (string[] batch in eventIds.Distinct().Chunk(LoadBatch))
        {
            foreach ((string id, string json) in await db.Events.IgnoreQueryFilters().AsNoTracking().Where(e => batch.Contains(e.EventId)).Select(e => new ValueTuple<string, string>(e.EventId, e.Json)).ToListAsync(cancellationToken))
            {
                JsonObject pdu = JsonNode.Parse(json)!.AsObject();
                pdu["event_id"] = id;
                events.Add(pdu);
            }
        }
        // Return events
        return events;
    }

    /// <summary>Return whether two states hold the same events.</summary>
    private static bool SameState(IReadOnlyDictionary<(string Type, string StateKey), string> first, IReadOnlyDictionary<(string Type, string StateKey), string> second) =>
        first.Count == second.Count && first.All(e => second.TryGetValue(e.Key, out string? other) && other == e.Value);

    /// <summary>Return a copy of an event without a field.</summary>
    private static JsonObject Without(JsonObject pdu, string field)
    {
        // Copy and remove
        JsonObject copy = pdu.DeepClone().AsObject();
        copy.Remove(field);
        // Return copy
        return copy;
    }
}
