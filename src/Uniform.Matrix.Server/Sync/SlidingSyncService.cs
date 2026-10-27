namespace Uniform.Matrix.Server.Sync;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4186">Simplified sliding sync</see> (MSC4186), which Element X and other new clients need: room lists in windows ordered by activity, room subscriptions, and extensions for to-device messages, encryption, account data, receipts and typing.</summary>
/// <remarks>
/// Rooms are built by <see cref="SyncService"/>: from the start for rooms the connection has not had yet, and since the client's position for the others, then reshaped.
/// Connections are kept in memory per user, device and <c>conn_id</c>; a position the server does not know, e.g. after a restart, gets <c>M_UNKNOWN_POS</c>, after which the client starts over.
/// Each request uses the largest timeline limit it asks for, and lazy members when any room config asks for them.
/// </remarks>
public sealed class SlidingSyncService
{
    /// <summary>Error code that makes clients start their sliding sync connection over.</summary>
    public const string UnknownPos = "M_UNKNOWN_POS";
    private const string LazyMembers = "$LAZY";
    private const string Me = "$ME";
    private const string Wildcard = "*";
    private readonly SyncService sync;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<(string UserId, string DeviceId, string ConnectionId), Connection> connections = new();

    /// <summary>What one connection has had: its last position and the rooms sent so far.</summary>
    private sealed class Connection
    {
        /// <summary>Guards the connection while a request uses it.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>Position last answered.</summary>
        public long Position { get; set; }
        /// <summary>Rooms sent at least once.</summary>
        public HashSet<string> Sent { get; } = [];
    }

    /// <summary>What a list or subscription asks of its rooms.</summary>
    /// <param name="RequiredState">Pairs of event type and state key, with <c>*</c>, <c>$LAZY</c> and <c>$ME</c>.</param>
    /// <param name="TimelineLimit">Most timeline events.</param>
    private sealed record RoomConfig(IReadOnlyList<(string Type, string StateKey)> RequiredState, int TimelineLimit);

    /// <summary>Create the service.</summary>
    public SlidingSyncService(SyncService sync, IDbContextFactory<ServerDbContext> contexts, ServerStream stream, TimeProvider time)
    {
        // Store dependencies
        this.sync = sync;
        this.contexts = contexts;
        this.stream = stream;
        this.time = time;
    }

    /// <summary>Answer a sliding sync request; wait up to <paramref name="timeout"/> for something new when there is nothing yet.</summary>
    /// <param name="requester">User and device.</param>
    /// <param name="pos">Position of the previous answer, or null to start.</param>
    /// <param name="timeout">How long to wait for news.</param>
    /// <param name="body">The request: <c>conn_id</c>, <c>lists</c>, <c>room_subscriptions</c> and <c>extensions</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> SyncAsync(Requester requester, string? pos, TimeSpan timeout, JsonObject body, CancellationToken cancellationToken)
    {
        // The connection, which must know the position
        string connectionId = body["conn_id"]?.GetValue<string>() ?? "";
        (string, string, string) key = (requester.UserId, requester.DeviceId, connectionId);
        long? from = pos is null ? null : SyncService.ParseToken(pos);
        Connection connection = from is null ? connections[key] = new Connection() : connections.TryGetValue(key, out Connection? known) && known.Position == from ? known : throw new MatrixException(HttpStatusCode.BadRequest, UnknownPos, "Unknown position; start the connection over.");

        // One request at a time per connection; wait while there is nothing new
        await connection.Gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset deadline = time.GetUtcNow() + timeout;
            long current = await stream.GetCurrentAsync(cancellationToken);
            while (true)
            {
                (JsonObject response, bool news) = await BuildAsync(requester, connection, from, current, body, cancellationToken);
                TimeSpan left = deadline - time.GetUtcNow();
                if (news || from is null || left <= TimeSpan.Zero)
                {
                    connection.Position = current;
                    return response;
                }
                current = await stream.WaitPastAsync(current, left, cancellationToken);
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <summary>Build one answer from <paramref name="from"/> to <paramref name="current"/>; return it and whether it has anything new.</summary>
    private async Task<(JsonObject Response, bool News)> BuildAsync(Requester requester, Connection connection, long? from, long current, JsonObject body, CancellationToken cancellationToken)
    {
        // The user's rooms by activity, and which are direct chats
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<(string RoomId, string Membership, long Bump)> rooms = await RoomsByActivityAsync(db, requester.UserId, cancellationToken);
        HashSet<string> direct = await DirectRoomsAsync(db, requester.UserId, cancellationToken);

        // Windows of the lists, then subscriptions, each room with the config that brought it
        JsonObject lists = [];
        Dictionary<string, RoomConfig> wanted = [];
        foreach ((string name, JsonNode? listNode) in body["lists"] as JsonObject ?? [])
        {
            if (listNode is not JsonObject list) continue;
            List<(string RoomId, string Membership, long Bump)> matching = [.. rooms.Where(r => Matches(list["filters"] as JsonObject, r.Membership, direct.Contains(r.RoomId)))];
            lists[name] = new JsonObject { ["count"] = matching.Count };
            RoomConfig config = Config(list);
            foreach (JsonNode? range in list["ranges"] as JsonArray ?? [new JsonArray(0, 19)])
            {
                int start = Math.Max(0, (int)(range?[0]?.GetValue<long>() ?? 0));
                int end = (int)Math.Min(range?[1]?.GetValue<long>() ?? 0, matching.Count - 1);
                for (int i = start; i <= end; i++) wanted.TryAdd(matching[i].RoomId, config);
            }
        }
        HashSet<string> mine = [.. rooms.Select(r => r.RoomId)];
        foreach ((string roomId, JsonNode? subscription) in body["room_subscriptions"] as JsonObject ?? [])
        {
            if (subscription is JsonObject config && mine.Contains(roomId)) wanted[roomId] = Config(config);
        }

        // New rooms from the start, the others since the position, through the classic sync
        int timelineLimit = wanted.Count == 0 ? 1 : wanted.Values.Max(c => c.TimelineLimit);
        bool lazy = wanted.Values.Any(c => c.RequiredState.Contains(("m.room.member", LazyMembers)));
        HashSet<string> initial = [.. wanted.Keys.Where(r => from is null || !connection.Sent.Contains(r))];
        HashSet<string> known = [.. wanted.Keys.Except(initial)];
        SyncFilter filter = new() { TimelineLimit = timelineLimit, LazyLoadMembers = lazy };
        JsonObject? first = initial.Count > 0 ? await sync.BuildAsync(requester, null, current, filter with { Rooms = initial }, fullState: false, cancellationToken) : null;
        JsonObject? since = from is long position ? await sync.BuildAsync(requester, position, current, filter with { Rooms = known.Count > 0 ? known : new HashSet<string> { "" } }, fullState: false, cancellationToken) : null;

        // Rooms reshaped
        JsonObject roomsOut = [];
        Dictionary<string, long> bumps = rooms.ToDictionary(r => r.RoomId, r => r.Bump);
        foreach ((JsonObject? result, bool isInitial) in new[] { (first, true), (since, false) })
        {
            foreach (string section in new[] { "join", "invite", "leave" })
            {
                foreach ((string roomId, JsonNode? room) in result?["rooms"]?[section] as JsonObject ?? [])
                {
                    if (room is not JsonObject classic || !wanted.TryGetValue(roomId, out RoomConfig? config)) continue;
                    roomsOut[roomId] = Reshape(requester.UserId, classic, section, isInitial, config, bumps.GetValueOrDefault(roomId), direct.Contains(roomId));
                    connection.Sent.Add(roomId);
                }
            }
        }

        // Extensions, each when enabled
        JsonObject extensions = await ExtensionsAsync(db, requester, from, current, body["extensions"] as JsonObject ?? [], since ?? first, roomsOut, cancellationToken);
        bool news = roomsOut.Count > 0 || extensions.Any(e => e.Value is JsonObject value && HasNews(e.Key, value));
        // Return answer
        return (new JsonObject { ["pos"] = SyncService.Token(current), ["lists"] = lists, ["rooms"] = roomsOut, ["extensions"] = extensions }, news);
    }

    /// <summary>Return a classic sync room as a sliding sync room.</summary>
    private static JsonObject Reshape(string userId, JsonObject classic, string section, bool initial, RoomConfig config, long bump, bool isDirect)
    {
        // Invites carry their stripped state only
        JsonObject room = new() { ["initial"] = initial, ["bump_stamp"] = bump };
        if (isDirect) room["is_dm"] = true;
        if (section == "invite")
        {
            room["invite_state"] = classic["invite_state"]?["events"]?.DeepClone() ?? new JsonArray();
            return room;
        }

        // State at the end: the state section, then state in the timeline on top, as the config asks
        JsonArray timeline = classic["timeline"]?["events"] as JsonArray ?? [];
        Dictionary<(string, string), JsonObject> state = [];
        foreach (JsonObject e in (classic["state"]?["events"] as JsonArray ?? []).Concat(timeline).OfType<JsonObject>())
        {
            if (e["state_key"]?.GetValue<string>() is string stateKey) state[(e["type"]!.GetValue<string>(), stateKey)] = e;
        }
        HashSet<string> senders = [.. timeline.OfType<JsonObject>().Select(e => e["sender"]?.GetValue<string>()).OfType<string>()];
        room["required_state"] = new JsonArray([.. state.Where(s => IsRequired(config, s.Key, userId, senders)).Select(s => (JsonNode)s.Value.DeepClone())]);
        if (state.TryGetValue(("m.room.name", ""), out JsonObject? name) && name["content"]?["name"]?.GetValue<string>() is { Length: > 0 } roomName) room["name"] = roomName;
        if (state.TryGetValue(("m.room.avatar", ""), out JsonObject? avatar) && avatar["content"]?["url"]?.GetValue<string>() is { Length: > 0 } url) room["avatar"] = url;

        // Timeline, counts and heroes
        room["timeline"] = timeline.DeepClone();
        room["limited"] = classic["timeline"]?["limited"]?.GetValue<bool>() ?? false;
        if (classic["timeline"]?["prev_batch"] is JsonNode prevBatch) room["prev_batch"] = prevBatch.DeepClone();
        room["num_live"] = initial ? 0 : timeline.Count;
        room["notification_count"] = classic["unread_notifications"]?["notification_count"]?.DeepClone() ?? 0;
        room["highlight_count"] = classic["unread_notifications"]?["highlight_count"]?.DeepClone() ?? 0;
        if (classic["summary"] is JsonObject summary)
        {
            if (summary["m.joined_member_count"] is JsonNode joined) room["joined_count"] = joined.DeepClone();
            if (summary["m.invited_member_count"] is JsonNode invited) room["invited_count"] = invited.DeepClone();
            if (summary["m.heroes"] is JsonArray heroes) room["heroes"] = new JsonArray([.. heroes.Select(h => (JsonNode)Hero(h!.GetValue<string>(), state))]);
        }
        // Return room
        return room;
    }

    /// <summary>Return a hero with the name and avatar of their member event, when it is in the state sent.</summary>
    private static JsonObject Hero(string userId, Dictionary<(string, string), JsonObject> state)
    {
        // User ID, then what the member event says
        JsonObject hero = new() { ["user_id"] = userId };
        if (state.TryGetValue(("m.room.member", userId), out JsonObject? member))
        {
            if (member["content"]?["displayname"] is JsonNode displayName) hero["displayname"] = displayName.DeepClone();
            if (member["content"]?["avatar_url"] is JsonNode avatarUrl) hero["avatar_url"] = avatarUrl.DeepClone();
        }
        // Return hero
        return hero;
    }

    /// <summary>Return whether a state event's key is asked for by the config.</summary>
    private static bool IsRequired(RoomConfig config, (string Type, string StateKey) key, string userId, HashSet<string> senders) => config.RequiredState.Any(r =>
        (r.Type == Wildcard || r.Type == key.Type) && (r.StateKey == Wildcard || r.StateKey == key.StateKey || r.StateKey == Me && key.StateKey == userId || r.StateKey == LazyMembers && senders.Contains(key.StateKey)));

    /// <summary>Return the extensions the request enables.</summary>
    private static async Task<JsonObject> ExtensionsAsync(ServerDbContext db, Requester requester, long? from, long current, JsonObject requested, JsonObject? classic, JsonObject rooms, CancellationToken cancellationToken)
    {
        // To-device messages since the extension's own position
        JsonObject extensions = [];
        if (requested["to_device"]?["enabled"]?.GetValue<bool>() == true)
        {
            long? toDeviceSince = requested["to_device"]?["since"]?.GetValue<string>() is string since ? SyncService.ParseToken(since) : null;
            IReadOnlyList<JsonObject> messages = await ToDeviceService.TakeAsync(db, requester.UserId, requester.DeviceId, toDeviceSince, current, cancellationToken);
            extensions["to_device"] = new JsonObject { ["next_batch"] = SyncService.Token(current), ["events"] = new JsonArray([.. messages.Select(m => (JsonNode)m.DeepClone())]) };
        }

        // Device lists and key counts
        if (requested["e2ee"]?["enabled"]?.GetValue<bool>() == true)
        {
            (IReadOnlyList<string> changed, IReadOnlyList<string> left) = from is long since ? await ToDeviceService.GetDeviceListsAsync(db, requester.UserId, since, current, cancellationToken) : ([], []);
            extensions["e2ee"] = new JsonObject
            {
                ["device_lists"] = new JsonObject { ["changed"] = new JsonArray([.. changed.Select(u => (JsonNode)u)]), ["left"] = new JsonArray([.. left.Select(u => (JsonNode)u)]) },
                ["device_one_time_keys_count"] = (await DeviceKeyService.CountOneTimeKeysAsync(db, requester.UserId, requester.DeviceId, cancellationToken))["one_time_key_counts"]!.DeepClone(),
                ["device_unused_fallback_key_types"] = await DeviceKeyService.UnusedFallbackKeyTypesAsync(db, requester.UserId, requester.DeviceId, cancellationToken),
            };
        }

        // Account data, receipts and typing from the classic sync of the same range
        if (requested["account_data"]?["enabled"]?.GetValue<bool>() == true)
        {
            JsonObject roomData = [];
            foreach ((string roomId, JsonNode? room) in classic?["rooms"]?["join"] as JsonObject ?? [])
            {
                if (rooms.ContainsKey(roomId) && room?["account_data"]?["events"] is JsonArray { Count: > 0 } events) roomData[roomId] = events.DeepClone();
            }
            extensions["account_data"] = new JsonObject { ["global"] = classic?["account_data"]?["events"]?.DeepClone() ?? new JsonArray(), ["rooms"] = roomData };
        }
        foreach ((string extension, string type) in new[] { ("receipts", "m.receipt"), ("typing", "m.typing") })
        {
            if (requested[extension]?["enabled"]?.GetValue<bool>() != true) continue;
            JsonObject byRoom = [];
            foreach ((string roomId, JsonNode? room) in classic?["rooms"]?["join"] as JsonObject ?? [])
            {
                if (room?["ephemeral"]?["events"] is JsonArray ephemeral && ephemeral.OfType<JsonObject>().FirstOrDefault(e => e["type"]?.GetValue<string>() == type) is JsonObject found) byRoom[roomId] = found.DeepClone();
            }
            extensions[extension] = new JsonObject { ["rooms"] = byRoom };
        }
        // Return extensions
        return extensions;
    }

    /// <summary>Return whether an extension has anything a client has to see now.</summary>
    private static bool HasNews(string name, JsonObject value) => name switch
    {
        "to_device" => value["events"] is JsonArray { Count: > 0 },
        "e2ee" => value["device_lists"]?["changed"] is JsonArray { Count: > 0 } || value["device_lists"]?["left"] is JsonArray { Count: > 0 },
        "account_data" => value["global"] is JsonArray { Count: > 0 } || value["rooms"] is JsonObject { Count: > 0 },
        _ => value["rooms"] is JsonObject { Count: > 0 },
    };

    /// <summary>Return the user's joined and invited rooms, most recent activity first.</summary>
    private static async Task<List<(string RoomId, string Membership, long Bump)>> RoomsByActivityAsync(ServerDbContext db, string userId, CancellationToken cancellationToken)
    {
        // Memberships here, and invites to rooms on other servers
        List<(string RoomId, string Membership, long Stream)> memberships = [.. (await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && (s.Membership == Memberships.Join || s.Membership == Memberships.Invite)).Select(s => new { s.RoomId, s.Membership, s.Stream }).ToListAsync(cancellationToken)).Select(m => (m.RoomId, m.Membership!, m.Stream))];
        HashSet<string> local = [.. memberships.Select(m => m.RoomId)];
        memberships.AddRange((await db.RemoteInvites.AsNoTracking().Where(i => i.UserId == userId && i.Membership == Memberships.Invite).Select(i => new { i.RoomId, i.Stream }).ToListAsync(cancellationToken)).Where(i => !local.Contains(i.RoomId)).Select(i => (i.RoomId, Memberships.Invite, i.Stream)));
        HashSet<string> forgotten = [.. await db.ForgottenRooms.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.RoomId).ToListAsync(cancellationToken)];

        // The latest event of each joined room, else the membership's position
        List<string> joined = [.. memberships.Where(m => m.Membership == Memberships.Join).Select(m => m.RoomId)];
        Dictionary<string, long> latest = await db.Events.AsNoTracking().Where(e => joined.Contains(e.RoomId)).GroupBy(e => e.RoomId).Select(g => new { RoomId = g.Key, Stream = g.Max(e => e.Stream) }).ToDictionaryAsync(g => g.RoomId, g => g.Stream, cancellationToken);
        // Return newest first
        return [.. memberships.Where(m => !forgotten.Contains(m.RoomId)).Select(m => (m.RoomId, m.Membership, Math.Max(m.Stream, latest.GetValueOrDefault(m.RoomId)))).OrderByDescending(m => m.Item3).ThenBy(m => m.RoomId, StringComparer.Ordinal)];
    }

    /// <summary>Return the rooms the user's <c>m.direct</c> account data marks as direct chats.</summary>
    private static async Task<HashSet<string>> DirectRoomsAsync(ServerDbContext db, string userId, CancellationToken cancellationToken)
    {
        // Room IDs of every listed user
        string? json = await db.AccountData.AsNoTracking().Where(a => a.UserId == userId && a.RoomId == "" && a.Type == "m.direct").Select(a => a.Json).SingleOrDefaultAsync(cancellationToken);
        JsonObject content = json is null ? [] : JsonNode.Parse(json) as JsonObject ?? [];
        // Return rooms
        return [.. content.SelectMany(u => u.Value as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>()];
    }

    /// <summary>Return whether a room passes a list's filters; <c>is_invite</c> and <c>is_dm</c> are understood, others ignored.</summary>
    private static bool Matches(JsonObject? filters, string membership, bool isDirect)
    {
        // Each given filter
        if (filters?["is_invite"]?.GetValue<bool>() is bool invite && invite != (membership == Memberships.Invite)) return false;
        // Return result
        return filters?["is_dm"]?.GetValue<bool>() is not bool dm || dm == isDirect;
    }

    /// <summary>Return the room config of a list or subscription.</summary>
    private static RoomConfig Config(JsonObject config)
    {
        // Pairs of type and state key, and the timeline limit
        List<(string, string)> required = [.. (config["required_state"] as JsonArray ?? []).OfType<JsonArray>().Where(p => p.Count == 2).Select(p => (p[0]!.GetValue<string>(), p[1]!.GetValue<string>()))];
        int limit = (int)Math.Clamp(config["timeline_limit"]?.GetValue<long>() ?? 1, 0, 100);
        // Return config
        return new RoomConfig(required, limit);
    }
}
