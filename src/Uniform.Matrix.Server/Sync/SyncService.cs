namespace Uniform.Matrix.Server.Sync;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#syncing">Sync</see>, room history and event context for local users.</summary>
/// <remarks>Tokens are <c>s</c> and a <see cref="ServerStream"/> position. <c>prev_batch</c> and pagination tokens use the same form: <c>/messages</c> backwards from <c>sN</c> returns events before position <c>N</c>.</remarks>
public sealed class SyncService
{
    private const int MaxHeroes = 5;
    private static readonly string[] StrippedStateTypes = [EventTypes.RoomCreate, "m.room.join_rules", EventTypes.RoomCanonicalAlias, "m.room.avatar", EventTypes.RoomName, EventTypes.RoomEncryption, EventTypes.RoomTopic];
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly StateCache cache;
    private readonly TypingService typing;
    private readonly PushRuleService pushRules;
    private readonly PresenceService presence;
    private readonly TimeProvider time;

    /// <summary>Create the service.</summary>
    public SyncService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, StateCache cache, TypingService typing, PushRuleService pushRules, PresenceService presence, TimeProvider time)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.cache = cache;
        this.typing = typing;
        this.pushRules = pushRules;
        this.presence = presence;
        this.time = time;
    }

    /// <summary>Return the sync response for <paramref name="requester"/>, waiting up to <paramref name="timeout"/> for something new after <paramref name="since"/>.</summary>
    public async Task<JsonObject> SyncAsync(Requester requester, string? since, TimeSpan timeout, SyncFilter filter, bool fullState, CancellationToken cancellationToken)
    {
        // Assert token
        long? from = since is null ? null : ParseToken(since);

        // Initial and full-state syncs answer at once
        long current = await stream.GetCurrentAsync(cancellationToken);
        if (from is null || fullState) return await BuildAsync(requester, from, current, filter, fullState, cancellationToken);

        // Incremental: wait until something reaches this user, or time is up
        DateTimeOffset deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            JsonObject response = await BuildAsync(requester, from, current, filter, fullState: false, cancellationToken);
            TimeSpan left = deadline - time.GetUtcNow();
            if (HasContent(response) || left <= TimeSpan.Zero) return response;
            current = await stream.WaitPastAsync(current, left, cancellationToken);
        }
    }

    /// <summary>Return room history for <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidmessages">/messages</see>: events before <paramref name="from"/> going backwards, or from it going forwards.</summary>
    public async Task<JsonObject> GetMessagesAsync(Requester requester, string roomId, string? from, string? to, bool backwards, int limit, SyncFilter filter, CancellationToken cancellationToken)
    {
        // Membership and range
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CurrentStateRow member = await GetMemberAsync(db, requester.UserId, roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not a member of the room.");
        long until = member.Membership == Memberships.Join ? long.MaxValue : member.Stream;
        long start = from is null ? (backwards ? until == long.MaxValue ? await stream.GetCurrentAsync(cancellationToken) + 1 : until + 1 : 0) : ParseToken(from);
        long? end = to is null ? null : ParseToken(to);

        // Page of visible events
        Visibility visibility = new(db, cache, requester.UserId, member.Membership);
        IQueryable<EventRow> query = db.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Stream <= until);
        query = backwards ? query.Where(e => e.Stream < start && (end == null || e.Stream >= end)).OrderByDescending(e => e.Stream) : query.Where(e => e.Stream >= start && (end == null || e.Stream < end)).OrderBy(e => e.Stream);
        (List<EventRow> page, bool more) = await TakeVisibleAsync(query, visibility, filter, limit, cancellationToken);

        // Chunk, tokens and members for lazy loading
        JsonObject response = new() { ["chunk"] = new JsonArray([.. await FormatAsync(db, requester, page, cancellationToken)]), ["start"] = from ?? Token(start) };
        if (more && page.Count > 0) response["end"] = Token(backwards ? page[^1].Stream : page[^1].Stream + 1);
        if (filter.LazyLoadMembers && page.Count > 0) response["state"] = new JsonArray([.. await MembersForAsync(db, requester, page, page.Max(e => e.StateGroup), cancellationToken)]);
        // Return page
        return response;
    }

    /// <summary>Return an event with events before and after it, and the state at it, for <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidcontexteventid">/context</see>.</summary>
    public async Task<JsonObject> GetContextAsync(Requester requester, string roomId, string eventId, int limit, SyncFilter filter, CancellationToken cancellationToken)
    {
        // Visible event of this room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CurrentStateRow member = await GetMemberAsync(db, requester.UserId, roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not a member of the room.");
        Visibility visibility = new(db, cache, requester.UserId, member.Membership);
        EventRow target = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId && e.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Event not found.");
        if (!await visibility.CanSeeAsync(target, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Event not found.");

        // Half the limit on each side
        long until = member.Membership == Memberships.Join ? long.MaxValue : member.Stream;
        int side = Math.Max(1, limit / 2);
        (List<EventRow> before, _) = await TakeVisibleAsync(db.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Stream < target.Stream).OrderByDescending(e => e.Stream), visibility, filter, side, cancellationToken);
        (List<EventRow> after, _) = await TakeVisibleAsync(db.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Stream > target.Stream && e.Stream <= until).OrderBy(e => e.Stream), visibility, filter, side, cancellationToken);

        // State at the event
        IReadOnlyDictionary<(string Type, string StateKey), string> state = await StateGroups.LoadAsync(db, target.StateGroup, cache, cancellationToken);
        IEnumerable<string> stateIds = filter.LazyLoadMembers ? state.Where(s => s.Key.Type != EventTypes.RoomMember || before.Concat(after).Append(target).Any(e => e.Sender == s.Key.StateKey) || s.Key.StateKey == requester.UserId).Select(s => s.Value) : state.Values;
        // Return context
        return new JsonObject
        {
            ["event"] = (await FormatAsync(db, requester, [target], cancellationToken)).Single(),
            ["events_before"] = new JsonArray([.. await FormatAsync(db, requester, before, cancellationToken)]),
            ["events_after"] = new JsonArray([.. await FormatAsync(db, requester, after, cancellationToken)]),
            ["state"] = new JsonArray([.. await LoadFormattedAsync(db, requester, stateIds, cancellationToken)]),
            ["start"] = Token(before.Count > 0 ? before[^1].Stream : target.Stream),
            ["end"] = Token((after.Count > 0 ? after[^1].Stream : target.Stream) + 1),
        };
    }

    /// <summary>Store a filter of <paramref name="userId"/>; return its ID.</summary>
    public async Task<string> CreateFilterAsync(string userId, JsonObject filter, CancellationToken cancellationToken)
    {
        // Assert it parses
        SyncFilter.Parse(filter);

        // Next number of the user
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        int count = await db.Filters.CountAsync(f => f.UserId == userId, cancellationToken);
        string filterId = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        db.Filters.Add(new FilterRow { UserId = userId, FilterId = filterId, Json = filter.ToJsonString() });
        await db.SaveChangesAsync(cancellationToken);
        // Return ID
        return filterId;
    }

    /// <summary>Return a stored filter's JSON; throw <c>M_NOT_FOUND</c> for unknown IDs.</summary>
    public async Task<JsonObject> GetFilterAsync(string userId, string filterId, CancellationToken cancellationToken)
    {
        // Own filters only
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string json = await db.Filters.AsNoTracking().Where(f => f.UserId == userId && f.FilterId == filterId).Select(f => f.Json).SingleOrDefaultAsync(cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Filter not found.");
        // Return filter
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>Resolve a <c>filter</c> query parameter: inline JSON, a stored filter ID, or none.</summary>
    public async Task<SyncFilter> ResolveFilterAsync(string userId, string? filter, CancellationToken cancellationToken)
    {
        // Inline JSON starts with a brace
        if (string.IsNullOrEmpty(filter)) return SyncFilter.Default;
        if (!filter.TrimStart().StartsWith('{')) return SyncFilter.Parse(await GetFilterAsync(userId, filter, cancellationToken));
        JsonObject json = JsonNode.Parse(filter) as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Filter is not an object.");
        // Return parsed
        return SyncFilter.Parse(json);
    }

    /// <summary>Build one response from <paramref name="from"/> (exclusive) to <paramref name="current"/>.</summary>
    internal async Task<JsonObject> BuildAsync(Requester requester, long? from, long current, SyncFilter filter, bool fullState, CancellationToken cancellationToken)
    {
        // The user's rooms, minus forgotten ones
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<CurrentStateRow> memberships = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == requester.UserId).ToListAsync(cancellationToken);
        HashSet<string> forgotten = [.. await db.ForgottenRooms.AsNoTracking().Where(f => f.UserId == requester.UserId).Select(f => f.RoomId).ToListAsync(cancellationToken)];
        JsonObject ruleset = await pushRules.GetRulesetAsync(requester.UserId, cancellationToken);

        // One section per room with something to say
        JsonObject join = [], invite = [], leave = [];
        foreach (CurrentStateRow member in memberships)
        {
            if (forgotten.Contains(member.RoomId) || !filter.IncludesRoom(member.RoomId) || member.Stream > current) continue;
            switch (member.Membership)
            {
                case Memberships.Join:
                    bool initial = from is null || fullState || member.Stream > from;
                    if (await BuildRoomAsync(db, requester, member, initial ? null : from, current, filter, ruleset, cancellationToken) is JsonObject joined) join[member.RoomId] = joined;
                    break;
                case Memberships.Invite when from is null || member.Stream > from:
                    invite[member.RoomId] = new JsonObject { ["invite_state"] = new JsonObject { ["events"] = new JsonArray([.. await StrippedStateAsync(db, member, cancellationToken)]) } };
                    break;
                case Memberships.Leave or Memberships.Ban when from is null ? filter.IncludeLeave : member.Stream > from:
                    if (await BuildRoomAsync(db, requester, member, from, member.Stream, filter, ruleset, cancellationToken) is JsonObject left) leave[member.RoomId] = left;
                    break;
            }
        }

        // Invites from other servers to rooms not here yet, and their rejections once; once the room is here, its own state says it
        long invitesSince = from ?? -1;
        foreach (RemoteInviteRow remote in await db.RemoteInvites.AsNoTracking().Where(i => i.UserId == requester.UserId && i.Stream > invitesSince && i.Stream <= current).ToListAsync(cancellationToken))
        {
            if (!filter.IncludesRoom(remote.RoomId) || memberships.Any(m => m.RoomId == remote.RoomId)) continue;
            if (remote.Membership == Memberships.Invite) invite[remote.RoomId] = new JsonObject { ["invite_state"] = new JsonObject { ["events"] = RemoteInviteState(remote) } };
            else if (from is not null) leave[remote.RoomId] = new JsonObject { ["timeline"] = new JsonObject { ["events"] = new JsonArray() }, ["state"] = new JsonObject { ["events"] = new JsonArray() } };
        }

        // Global account data with the push rules as they apply now, always in initial syncs; the device's messages and keys, and key changes of others
        List<JsonObject> accountData = [.. await AccountDataEventsAsync(db, requester.UserId, "", from, current, cancellationToken)];
        JsonObject pushRulesEvent = new() { ["type"] = PushRuleService.AccountDataType, ["content"] = new JsonObject { ["global"] = ruleset.DeepClone() } };
        int storedRules = accountData.FindIndex(e => e["type"]?.GetValue<string>() == PushRuleService.AccountDataType);
        if (storedRules >= 0) accountData[storedRules] = pushRulesEvent;
        else if (from is null) accountData.Add(pushRulesEvent);
        IReadOnlyList<JsonObject> toDevice = await ToDeviceService.TakeAsync(db, requester.UserId, requester.DeviceId, from, current, cancellationToken);
        (IReadOnlyList<string> changedUsers, IReadOnlyList<string> leftUsers) = from is long since ? await ToDeviceService.GetDeviceListsAsync(db, requester.UserId, since, current, cancellationToken) : ([], []);
        JsonNode keyCounts = (await DeviceKeyService.CountOneTimeKeysAsync(db, requester.UserId, requester.DeviceId, cancellationToken))["one_time_key_counts"]!.DeepClone();
        // Return response
        return new JsonObject
        {
            ["next_batch"] = Token(current),
            ["rooms"] = new JsonObject { ["join"] = join, ["invite"] = invite, ["leave"] = leave, ["knock"] = new JsonObject() },
            ["account_data"] = new JsonObject { ["events"] = new JsonArray([.. accountData]) },
            ["presence"] = new JsonObject { ["events"] = new JsonArray([.. await presence.GetEventsAsync(db, requester.UserId, from, current, cancellationToken)]) },
            ["to_device"] = new JsonObject { ["events"] = new JsonArray([.. toDevice]) },
            ["device_lists"] = new JsonObject { ["changed"] = new JsonArray([.. changedUsers.Select(u => (JsonNode)u)]), ["left"] = new JsonArray([.. leftUsers.Select(u => (JsonNode)u)]) },
            ["device_one_time_keys_count"] = keyCounts,
            ["device_unused_fallback_key_types"] = await DeviceKeyService.UnusedFallbackKeyTypesAsync(db, requester.UserId, requester.DeviceId, cancellationToken),
        };
    }

    /// <summary>Build a joined or left room's section: timeline, state, account data, ephemeral events, unread counts by <paramref name="ruleset"/> and summary; null when nothing changed since <paramref name="from"/>.</summary>
    private async Task<JsonObject?> BuildRoomAsync(ServerDbContext dbContext, Requester requester, CurrentStateRow member, long? from, long until, SyncFilter filter, JsonObject ruleset, CancellationToken cancellationToken)
    {
        // Anything new: events, account data, or receipts and typing of joined rooms
        string roomId = member.RoomId;
        bool joined = member.Membership == Memberships.Join;
        bool hasEvents = from is not long since || await dbContext.Events.AnyAsync(e => e.RoomId == roomId && e.Stream > since && e.Stream <= until, cancellationToken);
        IReadOnlyList<JsonObject> roomData = await AccountDataEventsAsync(dbContext, requester.UserId, roomId, from, until, cancellationToken);
        List<JsonObject> ephemeral = [];
        if (joined && await ReceiptService.GetReceiptEventAsync(dbContext, roomId, requester.UserId, from, until, cancellationToken) is JsonObject receipts) ephemeral.Add(receipts);
        if (joined && typing.GetTypingEvent(roomId, from) is JsonObject typingEvent) ephemeral.Add(typingEvent);
        if (!hasEvents && roomData.Count == 0 && ephemeral.Count == 0) return null;

        // Newest visible events of the range
        Visibility visibility = new(dbContext, cache, requester.UserId, member.Membership);
        IQueryable<EventRow> range = dbContext.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Stream <= until && (from == null || e.Stream > from)).OrderByDescending(e => e.Stream);
        (List<EventRow> newestFirst, bool limited) = await TakeVisibleAsync(range, visibility, filter, filter.TimelineLimit, cancellationToken);
        List<EventRow> timeline = [.. Enumerable.Reverse(newestFirst)];

        // State at the start of the timeline: all of it for initial syncs, else what changed since the token
        long startStream = timeline.Count > 0 ? timeline[0].Stream : until + 1;
        IReadOnlyDictionary<(string Type, string StateKey), string> startState = await StateBeforeAsync(dbContext, roomId, startStream, cancellationToken);
        IEnumerable<string> stateIds;
        if (from is null) stateIds = startState.Values;
        else if (!limited) stateIds = [];
        else
        {
            IReadOnlyDictionary<(string, string), string> tokenState = await StateBeforeAsync(dbContext, roomId, from.Value + 1, cancellationToken);
            stateIds = startState.Where(s => tokenState.GetValueOrDefault(s.Key) != s.Value).Select(s => s.Value);
        }

        // Only the needed members when lazy loading
        List<string> heroes = await HeroesAsync(dbContext, requester.UserId, roomId, cancellationToken);
        if (filter.LazyLoadMembers)
        {
            HashSet<string> wanted = [.. timeline.Select(e => e.Sender), requester.UserId, .. heroes];
            stateIds = stateIds.Where(id => !startState.Any(s => s.Value == id && s.Key.Type == EventTypes.RoomMember && !wanted.Contains(s.Key.StateKey))).Concat(from is null ? [] : wanted.Select(u => startState.GetValueOrDefault((EventTypes.RoomMember, u))).OfType<string>()).Distinct();
        }

        // Section with summary counts from the current state
        JsonObject section = new()
        {
            ["timeline"] = new JsonObject { ["events"] = new JsonArray([.. await FormatAsync(dbContext, requester, timeline, cancellationToken)]), ["limited"] = limited, ["prev_batch"] = Token(startStream) },
            ["state"] = new JsonObject { ["events"] = new JsonArray([.. await LoadFormattedAsync(dbContext, requester, stateIds, cancellationToken)]) },
            ["account_data"] = new JsonObject { ["events"] = new JsonArray([.. roomData]) },
        };
        if (!joined) return section;
        section["ephemeral"] = new JsonObject { ["events"] = new JsonArray([.. ephemeral]) };
        (int notifications, int highlights) = await PushRuleService.CountUnreadAsync(dbContext, ruleset, requester.UserId, roomId, cancellationToken);
        section["unread_notifications"] = new JsonObject { ["notification_count"] = notifications, ["highlight_count"] = highlights };
        section["summary"] = new JsonObject
        {
            ["m.heroes"] = new JsonArray([.. heroes.Select(h => (JsonNode)h)]),
            ["m.joined_member_count"] = await dbContext.CurrentState.CountAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join, cancellationToken),
            ["m.invited_member_count"] = await dbContext.CurrentState.CountAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Invite, cancellationToken),
        };
        // Return section
        return section;
    }

    /// <summary>Return account data events of the user changed after <paramref name="from"/> (all when null): global for an empty <paramref name="roomId"/>.</summary>
    private static async Task<IReadOnlyList<JsonObject>> AccountDataEventsAsync(ServerDbContext dbContext, string userId, string roomId, long? from, long until, CancellationToken cancellationToken)
    {
        // Changed rows in stream order
        List<AccountDataRow> rows = await dbContext.AccountData.AsNoTracking().Where(a => a.UserId == userId && a.RoomId == roomId && a.Stream <= until && (from == null || a.Stream > from)).OrderBy(a => a.Stream).ToListAsync(cancellationToken);
        // Return events
        return [.. rows.Select(r => new JsonObject { ["type"] = r.Type, ["content"] = JsonNode.Parse(r.Json) })];
    }

    /// <summary>Take up to <paramref name="limit"/> events the user may see, in the query's order; also says whether more exist.</summary>
    private static async Task<(List<EventRow> Events, bool More)> TakeVisibleAsync(IQueryable<EventRow> query, Visibility visibility, SyncFilter filter, int limit, CancellationToken cancellationToken)
    {
        // Batches, so invisible events do not shorten the page
        List<EventRow> taken = [];
        int skip = 0;
        while (true)
        {
            List<EventRow> batch = await query.Skip(skip).Take(limit + 1).ToListAsync(cancellationToken);
            skip += batch.Count;
            foreach (EventRow row in batch)
            {
                if (!filter.IncludesType(row.Type) || !await visibility.CanSeeAsync(row, cancellationToken)) continue;
                if (taken.Count == limit) return (taken, true);
                taken.Add(row);
            }
            // Done when the query ran out
            if (batch.Count <= limit) return (taken, false);
        }
    }

    /// <summary>Return the room's state just before stream position <paramref name="position"/>.</summary>
    private async Task<IReadOnlyDictionary<(string Type, string StateKey), string>> StateBeforeAsync(ServerDbContext dbContext, string roomId, long position, CancellationToken cancellationToken)
    {
        // State after the last earlier event
        long group = await dbContext.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Stream < position).OrderByDescending(e => e.Stream).Select(e => e.StateGroup).FirstOrDefaultAsync(cancellationToken);
        // Return state
        return await StateGroups.LoadAsync(dbContext, group, cache, cancellationToken);
    }

    /// <summary>Return up to five other members, joined or invited, for clients to name rooms without names.</summary>
    private static async Task<List<string>> HeroesAsync(ServerDbContext dbContext, string userId, string roomId, CancellationToken cancellationToken) =>
        await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey != userId && (s.Membership == Memberships.Join || s.Membership == Memberships.Invite))
            .OrderBy(s => s.Stream).Select(s => s.StateKey).Take(MaxHeroes).ToListAsync(cancellationToken);

    /// <summary>Return the member events of the senders of <paramref name="events"/> and the requester, from state group <paramref name="group"/>.</summary>
    private async Task<IReadOnlyList<JsonObject>> MembersForAsync(ServerDbContext dbContext, Requester requester, IReadOnlyList<EventRow> events, long group, CancellationToken cancellationToken)
    {
        // Member events of the senders
        IReadOnlyDictionary<(string Type, string StateKey), string> state = await StateGroups.LoadAsync(dbContext, group, cache, cancellationToken);
        HashSet<string> senders = [.. events.Select(e => e.Sender), requester.UserId];
        // Return events
        return await LoadFormattedAsync(dbContext, requester, senders.Select(s => state.GetValueOrDefault((EventTypes.RoomMember, s))).OfType<string>(), cancellationToken);
    }

    /// <summary>Return the stripped state of an invite from another server: what that server sent, and the invite itself.</summary>
    private static JsonArray RemoteInviteState(RemoteInviteRow remote)
    {
        // Sent state without the invitee's own membership, then the invite
        JsonArray events = [.. (JsonNode.Parse(remote.StrippedStateJson) as JsonArray ?? []).OfType<JsonObject>().Where(e => !(e["type"]?.GetValue<string>() == EventTypes.RoomMember && e["state_key"]?.GetValue<string>() == remote.UserId)).Select(e => e.DeepClone())];
        JsonObject invite = JsonNode.Parse(remote.EventJson)!.AsObject();
        events.Add(new JsonObject { ["type"] = EventTypes.RoomMember, ["state_key"] = remote.UserId, ["sender"] = remote.Sender, ["content"] = invite["content"]?.DeepClone() });
        // Return events
        return events;
    }

    /// <summary>Return the invite's stripped state: key room events and the invite itself, without IDs.</summary>
    private async Task<IReadOnlyList<JsonObject>> StrippedStateAsync(ServerDbContext dbContext, CurrentStateRow member, CancellationToken cancellationToken)
    {
        // State at the invite
        EventRow inviteRow = await dbContext.Events.AsNoTracking().SingleAsync(e => e.EventId == member.EventId, cancellationToken);
        IReadOnlyDictionary<(string Type, string StateKey), string> state = await StateGroups.LoadAsync(dbContext, inviteRow.StateGroup, cache, cancellationToken);
        List<string> ids = [.. StrippedStateTypes.Select(t => state.GetValueOrDefault((t, ""))).OfType<string>(), member.EventId];
        List<EventRow> rows = await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).ToListAsync(cancellationToken);

        // Type, state key, sender and content only
        // Return stripped events
        return [.. rows.Select(r => new JsonObject { ["type"] = r.Type, ["state_key"] = r.StateKey, ["sender"] = r.Sender, ["content"] = JsonNode.Parse(r.Json)!["content"]?.DeepClone() })];
    }

    /// <summary>Load events by ID in client format.</summary>
    private async Task<IReadOnlyList<JsonObject>> LoadFormattedAsync(ServerDbContext dbContext, Requester requester, IEnumerable<string> eventIds, CancellationToken cancellationToken)
    {
        // Rows in stream order
        List<string> ids = [.. eventIds.Distinct()];
        List<EventRow> rows = await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).OrderBy(e => e.Stream).ToListAsync(cancellationToken);
        // Return formatted
        return await FormatAsync(dbContext, requester, rows, cancellationToken);
    }

    /// <summary>Format events for the requester: transaction IDs of their device, redaction reasons and previous state contents.</summary>
    private async Task<IReadOnlyList<JsonObject>> FormatAsync(ServerDbContext dbContext, Requester requester, IReadOnlyList<EventRow> rows, CancellationToken cancellationToken)
    {
        // Transactions and redactions of these events
        List<string> ids = [.. rows.Select(r => r.EventId)];
        Dictionary<string, string> transactions = await dbContext.Transactions.AsNoTracking().Where(t => t.UserId == requester.UserId && t.DeviceId == requester.DeviceId && ids.Contains(t.EventId)).ToDictionaryAsync(t => t.EventId, t => t.TransactionId, cancellationToken);
        List<string> redactionIds = [.. rows.Select(r => r.RedactedBy).OfType<string>()];
        Dictionary<string, EventRow> redactions = await dbContext.Events.AsNoTracking().Where(e => redactionIds.Contains(e.EventId)).ToDictionaryAsync(e => e.EventId, cancellationToken);

        // Client form, with the content each state event replaced
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        List<JsonObject> formatted = [];
        foreach (EventRow row in rows)
        {
            JsonObject? prevContent = row.StateKey is null ? null : await ReplacedContentAsync(dbContext, row, cancellationToken);
            JsonObject? because = row.RedactedBy is string by && redactions.TryGetValue(by, out EventRow? redaction) ? ClientFormat.ToClient(redaction, now) : null;
            formatted.Add(ClientFormat.ToClient(row, now, transactions.GetValueOrDefault(row.EventId), because, prevContent));
        }
        // Return events
        return formatted;
    }

    /// <summary>Return the content of the state a state event replaced, or null for new state.</summary>
    private async Task<JsonObject?> ReplacedContentAsync(ServerDbContext dbContext, EventRow row, CancellationToken cancellationToken)
    {
        // State before the event
        IReadOnlyDictionary<(string Type, string StateKey), string> before = await StateBeforeAsync(dbContext, row.RoomId, row.Stream, cancellationToken);
        if (!before.TryGetValue((row.Type, row.StateKey!), out string? previous)) return null;
        string? json = await dbContext.Events.AsNoTracking().Where(e => e.EventId == previous).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        // Return content
        return json is null ? null : JsonNode.Parse(json)!["content"]?.DeepClone() as JsonObject;
    }

    /// <summary>Return the user's current membership row in a room, or null.</summary>
    private static Task<CurrentStateRow?> GetMemberAsync(ServerDbContext dbContext, string userId, string roomId, CancellationToken cancellationToken) =>
        dbContext.CurrentState.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId, cancellationToken);

    /// <summary>Return whether a response tells the client anything new.</summary>
    private static bool HasContent(JsonObject response)
    {
        // Any room section, account data, presence or device news
        JsonNode rooms = response["rooms"]!;
        if (response["account_data"]!["events"]!.AsArray().Count > 0 || response["to_device"]!["events"]!.AsArray().Count > 0) return true;
        if (response["presence"]!["events"]!.AsArray().Count > 0) return true;
        if (response["device_lists"]!["changed"]!.AsArray().Count > 0 || response["device_lists"]!["left"]!.AsArray().Count > 0) return true;
        // Return result
        return rooms["join"]!.AsObject().Count + rooms["invite"]!.AsObject().Count + rooms["leave"]!.AsObject().Count > 0;
    }

    /// <summary>Format a stream position as a token.</summary>
    public static string Token(long position) => $"s{position}";

    /// <summary>Parse a token; throw <c>M_INVALID_PARAM</c> for others.</summary>
    public static long ParseToken(string token) =>
        token.StartsWith('s') && long.TryParse(token.AsSpan(1), out long position) && position >= 0 ? position : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Invalid token {token}.");
}
