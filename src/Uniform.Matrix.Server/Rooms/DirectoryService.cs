namespace Uniform.Matrix.Server.Rooms;

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#room-aliases">Room aliases</see>, the <see href="https://spec.matrix.org/latest/client-server-api/#listing-rooms">public room directory</see> and <see href="https://spec.matrix.org/latest/client-server-api/#user-directory">user search</see>.</summary>
public sealed class DirectoryService
{
    private const int MaxAliasLength = 255;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly RoomService rooms;
    private readonly IOptions<ServerOptions> options;
    private readonly RemoteJoins remoteJoins;
    private readonly FederationClient federation;

    /// <summary>Create the service.</summary>
    public DirectoryService(IDbContextFactory<ServerDbContext> contexts, RoomService rooms, IOptions<ServerOptions> options, RemoteJoins remoteJoins, FederationClient federation)
    {
        // Store dependencies
        this.contexts = contexts;
        this.rooms = rooms;
        this.options = options;
        this.remoteJoins = remoteJoins;
        this.federation = federation;
    }

    /// <summary>Server name of local aliases.</summary>
    private string ServerName => options.Value.ServerName;

    /// <summary>Return the room of an alias and servers in it; aliases of other servers are asked from them. Throw <c>M_NOT_FOUND</c> for unknown aliases.</summary>
    public async Task<JsonObject> GetAliasAsync(string alias, CancellationToken cancellationToken)
    {
        // Other servers' aliases from their server
        string aliasServer = AssertAlias(alias);
        if (aliasServer != ServerName)
        {
            (string remoteRoomId, IReadOnlyList<string> remoteServers) = await remoteJoins.ResolveAliasAsync(alias, aliasServer, cancellationToken);
            return new JsonObject { ["room_id"] = remoteRoomId, ["servers"] = new JsonArray([.. remoteServers.Select(s => (JsonNode)s)]) };
        }
        // Return local room and its servers
        (string roomId, IReadOnlyList<string> servers) = await GetLocalAliasAsync(alias, cancellationToken);
        return new JsonObject { ["room_id"] = roomId, ["servers"] = new JsonArray([.. servers.Select(s => (JsonNode)s)]) };
    }

    /// <summary>Return the room of a local alias and the servers with members joined to it, this one first, as <c>query/directory</c> answers other servers; throw <c>M_NOT_FOUND</c> for unknown aliases.</summary>
    public async Task<(string RoomId, IReadOnlyList<string> Servers)> GetLocalAliasAsync(string alias, CancellationToken cancellationToken)
    {
        // Assert local and known
        if (AssertAlias(alias) != ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"{alias} is not an alias of this server.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomAliasRow row = await db.RoomAliases.AsNoTracking().SingleOrDefaultAsync(a => a.Alias == alias, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"Room alias {alias} not found.");

        // Servers of the joined members
        List<string> members = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == row.RoomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).ToListAsync(cancellationToken);
        List<string> servers = [ServerName, .. members.Select(m => UserId.TryParse(m, out UserId user) ? user.ServerName : null).OfType<string>().Where(s => s != ServerName).Distinct()];
        // Return room and servers
        return (row.RoomId, servers);
    }

    /// <summary>Create a local alias for a room the user is joined to; throw <c>409</c> when the alias exists.</summary>
    public async Task CreateAliasAsync(string userId, string alias, string roomId, CancellationToken cancellationToken)
    {
        // Assert valid alias of a joined room
        AssertLocalAlias(alias);
        if (await rooms.GetMembershipAsync(roomId, userId, cancellationToken) != Memberships.Join) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Join the room before adding an alias for it.");

        // Unique alias
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.RoomAliases.AnyAsync(a => a.Alias == alias, cancellationToken)) throw new MatrixException(HttpStatusCode.Conflict, ErrorCodes.Unknown, $"Room alias {alias} already exists.");
        db.RoomAliases.Add(new RoomAliasRow { Alias = alias, RoomId = roomId, Creator = userId });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Delete a local alias: its creator, a server admin, or a room member allowed to change the canonical alias may.</summary>
    public async Task DeleteAliasAsync(string userId, string alias, CancellationToken cancellationToken)
    {
        // Existing alias
        AssertLocalAlias(alias);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomAliasRow row = await db.RoomAliases.SingleOrDefaultAsync(a => a.Alias == alias, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"Room alias {alias} not found.");

        // Assert allowed
        bool allowed = row.Creator == userId || await IsAdminAsync(db, userId, cancellationToken) || await rooms.CanSendStateAsync(userId, row.RoomId, EventTypes.RoomCanonicalAlias, cancellationToken);
        if (!allowed) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not allowed to delete this alias.");
        db.RoomAliases.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return the local aliases of a room the user is joined to.</summary>
    public async Task<JsonObject> GetRoomAliasesAsync(string userId, string roomId, CancellationToken cancellationToken)
    {
        // Members only
        if (await rooms.GetMembershipAsync(roomId, userId, cancellationToken) != Memberships.Join) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not joined to the room.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> aliases = await db.RoomAliases.AsNoTracking().Where(a => a.RoomId == roomId).Select(a => a.Alias).OrderBy(a => a).ToListAsync(cancellationToken);
        // Return aliases
        return new JsonObject { ["aliases"] = new JsonArray([.. aliases.Select(a => (JsonNode)a)]) };
    }

    /// <summary>Return whether a room is listed in the directory: <c>public</c> or <c>private</c>.</summary>
    public async Task<JsonObject> GetVisibilityAsync(string roomId, CancellationToken cancellationToken)
    {
        // Known room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomRow room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        // Return visibility
        return new JsonObject { ["visibility"] = room.Published ? "public" : "private" };
    }

    /// <summary>List or unlists a room; a server admin, or a member allowed to change the canonical alias, may.</summary>
    public async Task SetVisibilityAsync(string userId, string roomId, string visibility, CancellationToken cancellationToken)
    {
        // Assert value and permission
        if (visibility is not ("public" or "private")) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "visibility must be public or private.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomRow room = await db.Rooms.SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        bool allowed = await IsAdminAsync(db, userId, cancellationToken) || await rooms.CanSendStateAsync(userId, roomId, EventTypes.RoomCanonicalAlias, cancellationToken);
        if (!allowed) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not allowed to change the room's directory visibility.");
        room.Published = visibility == "public";
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return a page of the public room directory, most members first.</summary>
    /// <param name="limit">Rooms per page; all when null.</param>
    /// <param name="since">Token of the page, <c>p</c> and an offset, from <c>next_batch</c> or <c>prev_batch</c>.</param>
    /// <param name="searchTerm">Text to find in the name, topic or canonical alias.</param>
    /// <param name="server">Server whose directory to list; other servers need federation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> GetPublicRoomsAsync(int? limit, string? since, string? searchTerm, string? server, CancellationToken cancellationToken)
    {
        // Other servers' directories from them
        if (server is not null && server != ServerName) return await GetRemotePublicRoomsAsync(server, limit, since, searchTerm, cancellationToken);

        // Assert page token
        int offset = since is null ? 0 : since.StartsWith('p') && int.TryParse(since.AsSpan(1), CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 ? parsed : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Invalid token {since}.");

        // Published rooms matching the search
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> roomIds = await db.Rooms.AsNoTracking().Where(r => r.Published).Select(r => r.RoomId).ToListAsync(cancellationToken);
        List<JsonObject> entries = [];
        foreach (string roomId in roomIds) entries.Add(await PublicEntryAsync(db, roomId, cancellationToken));
        if (!string.IsNullOrWhiteSpace(searchTerm)) entries = [.. entries.Where(e => new[] { "name", "topic", "canonical_alias" }.Any(k => e[k]?.GetValue<string>().Contains(searchTerm.Trim(), StringComparison.OrdinalIgnoreCase) == true))];
        entries = [.. entries.OrderByDescending(e => e["num_joined_members"]!.GetValue<int>()).ThenBy(e => e["room_id"]!.GetValue<string>(), StringComparer.Ordinal)];

        // Page with tokens on both sides
        int size = limit is > 0 ? limit.Value : entries.Count;
        JsonObject response = new() { ["chunk"] = new JsonArray([.. entries.Skip(offset).Take(size)]), ["total_room_count_estimate"] = entries.Count };
        if (offset + size < entries.Count) response["next_batch"] = $"p{offset + size}";
        if (offset > 0) response["prev_batch"] = $"p{Math.Max(0, offset - size)}";
        // Return page
        return response;
    }

    /// <summary>Return a page of the public room directory of <paramref name="server"/>, asked over <see href="https://spec.matrix.org/latest/server-server-api/#public-room-directory">federation</see>; the filtered form is used when <paramref name="searchTerm"/> is set.</summary>
    private async Task<JsonObject> GetRemotePublicRoomsAsync(string server, int? limit, string? since, string? searchTerm, CancellationToken cancellationToken)
    {
        // Assert a server name
        if (!ServerNames.TryParse(server, out _, out _)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{server} is not a server name.");

        // Search or list
        JsonNode? answer;
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            JsonObject body = new() { ["filter"] = new JsonObject { ["generic_search_term"] = searchTerm.Trim() } };
            if (limit is > 0) body["limit"] = limit.Value;
            if (since is not null) body["since"] = since;
            answer = await federation.SendAsync(HttpMethod.Post, server, "/_matrix/federation/v1/publicRooms", body, cancellationToken);
        }
        else
        {
            List<string> query = [];
            if (limit is > 0) query.Add($"limit={limit.Value}");
            if (since is not null) query.Add($"since={Uri.EscapeDataString(since)}");
            answer = await federation.SendAsync(HttpMethod.Get, server, "/_matrix/federation/v1/publicRooms" + (query.Count > 0 ? "?" + string.Join('&', query) : ""), null, cancellationToken);
        }

        // Return the other server's page; a malformed one has no rooms
        if (answer is not JsonObject page || page["chunk"] is not JsonArray) return new JsonObject { ["chunk"] = new JsonArray() };
        return page;
    }

    /// <summary>Return the <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/3266">summary</see> of a local room, as the room directory shows it with the user's membership, version and encryption; throw <c>M_NOT_FOUND</c> when the user may not see it.</summary>
    /// <param name="userId">Who asks, or null without a login.</param>
    /// <param name="roomIdOrAlias">Room ID or local alias.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> GetSummaryAsync(string? userId, string roomIdOrAlias, CancellationToken cancellationToken)
    {
        // Local room the user may see
        string roomId = await rooms.ResolveAsync(roomIdOrAlias, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        JsonObject summary = await PublicEntryAsync(db, roomId, cancellationToken);
        string? membership = userId is null ? null : await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId).Select(s => s.Membership).SingleOrDefaultAsync(cancellationToken);
        if (!CanPeek(summary, membership)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Room not found.");

        // Membership, version and encryption besides the directory fields
        RoomRow room = await db.Rooms.AsNoTracking().SingleAsync(r => r.RoomId == roomId, cancellationToken);
        summary["room_version"] = room.Version;
        summary["im.nheko.summary.room_version"] = room.Version;
        summary["membership"] = membership ?? Memberships.Leave;
        if (await StateContentAsync(db, roomId, EventTypes.RoomEncryption, "", cancellationToken) is JsonObject encryption && encryption["algorithm"]?.GetValue<string>() is string algorithm)
        {
            summary["encryption"] = algorithm;
            summary["im.nheko.summary.encryption"] = algorithm;
        }
        // Return summary
        return summary;
    }

    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1roomsroomidhierarchy">hierarchy</see> of a local space: the space and the local rooms its <c>m.space.child</c> events name, depth first, that the user may see.</summary>
    /// <param name="userId">Who asks.</param>
    /// <param name="roomId">The space.</param>
    /// <param name="maxDepth">Deepest level, the space being 0; unlimited when null.</param>
    /// <param name="limit">Most rooms per page.</param>
    /// <param name="from">Token of the page, <c>p</c> and an offset.</param>
    /// <param name="suggestedOnly">Only children marked <c>suggested</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> GetHierarchyAsync(string userId, string roomId, int? maxDepth, int limit, string? from, bool suggestedOnly, CancellationToken cancellationToken)
    {
        // Assert page token; the space must be visible
        int offset = from is null ? 0 : from.StartsWith('p') && int.TryParse(from.AsSpan(1), CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 ? parsed : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Invalid token {from}.");
        await GetSummaryAsync(userId, roomId, cancellationToken);

        // Depth first over children here, each room once
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<JsonObject> found = [];
        HashSet<string> seen = [];
        Stack<(string RoomId, int Depth)> next = new([(roomId, 0)]);
        while (next.Count > 0 && found.Count < offset + limit + 1 && found.Count < 1000)
        {
            (string current, int depth) = next.Pop();
            if (!seen.Add(current) || !await db.Rooms.AnyAsync(r => r.RoomId == current, cancellationToken)) continue;
            JsonObject entry = await PublicEntryAsync(db, current, cancellationToken);
            string? membership = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == current && s.Type == EventTypes.RoomMember && s.StateKey == userId).Select(s => s.Membership).SingleOrDefaultAsync(cancellationToken);
            if (current != roomId && !CanPeek(entry, membership)) continue;

            // Its children, suggested ones only when asked
            List<JsonObject> children = await ChildEventsAsync(db, current, suggestedOnly, cancellationToken);
            entry["children_state"] = new JsonArray([.. children]);
            found.Add(entry);
            if (maxDepth is int max && depth >= max) continue;
            foreach (JsonObject child in Enumerable.Reverse(children)) next.Push((child["state_key"]!.GetValue<string>(), depth + 1));
        }

        // Page with a token to the next
        JsonObject response = new() { ["rooms"] = new JsonArray([.. found.Skip(offset).Take(limit)]) };
        if (found.Count > offset + limit) response["next_batch"] = $"p{offset + limit}";
        // Return page
        return response;
    }

    /// <summary>Return a local space and its direct children for another server, as <see href="https://spec.matrix.org/latest/server-server-api/#get_matrixfederationv1hierarchyroomid">federation hierarchy</see> answers; rooms the server may not see are listed as inaccessible.</summary>
    public async Task<JsonObject> GetFederationHierarchyAsync(string origin, string roomId, bool suggestedOnly, CancellationToken cancellationToken)
    {
        // The space, which the server must be able to see
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Rooms.AnyAsync(r => r.RoomId == roomId, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Room not found.");
        JsonObject room = await PublicEntryAsync(db, roomId, cancellationToken);
        if (!await ServerMaySeeAsync(db, origin, roomId, room, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Room not found.");
        List<JsonObject> childEvents = await ChildEventsAsync(db, roomId, suggestedOnly, cancellationToken);
        room["children_state"] = new JsonArray([.. childEvents]);

        // Its children here, each seen or not
        JsonArray children = [], inaccessible = [];
        foreach (string child in childEvents.Select(c => c["state_key"]!.GetValue<string>()))
        {
            if (!await db.Rooms.AnyAsync(r => r.RoomId == child, cancellationToken)) continue;
            JsonObject entry = await PublicEntryAsync(db, child, cancellationToken);
            if (!await ServerMaySeeAsync(db, origin, child, entry, cancellationToken))
            {
                inaccessible.Add(child);
                continue;
            }
            entry["children_state"] = new JsonArray([.. await ChildEventsAsync(db, child, suggestedOnly, cancellationToken)]);
            children.Add(entry);
        }
        // Return space and children
        return new JsonObject { ["room"] = room, ["children"] = children, ["inaccessible_children"] = inaccessible };
    }

    /// <summary>Return whether another server may see a room: anyone for public, knockable or world-readable rooms, else servers with a member in it.</summary>
    private static async Task<bool> ServerMaySeeAsync(ServerDbContext dbContext, string origin, string roomId, JsonObject entry, CancellationToken cancellationToken)
    {
        // Open rooms, then members of the server
        if (CanPeek(entry, null)) return true;
        List<string> members = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).ToListAsync(cancellationToken);
        // Return whether one is of the origin
        return members.Any(m => UserId.TryParse(m, out UserId user) && user.ServerName == origin);
    }

    /// <summary>Return whether a user with <paramref name="membership"/> may see a room's summary: members and invitees, and anyone for public, knockable or world-readable rooms.</summary>
    private static bool CanPeek(JsonObject entry, string? membership) =>
        membership is Memberships.Join or Memberships.Invite or Memberships.Knock || entry["world_readable"]?.GetValue<bool>() == true || entry["join_rule"]?.GetValue<string>() is "public" or "knock" or "knock_restricted";

    /// <summary>Return the <c>m.space.child</c> events of a room that name a room, as stripped events, ordered by their <c>order</c>.</summary>
    private static async Task<List<JsonObject>> ChildEventsAsync(ServerDbContext dbContext, string roomId, bool suggestedOnly, CancellationToken cancellationToken)
    {
        // Current child state with content
        List<string> ids = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == "m.space.child").Select(s => s.EventId).ToListAsync(cancellationToken);
        List<JsonObject> children = [];
        foreach (string json in await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).Select(e => e.Json).ToListAsync(cancellationToken))
        {
            JsonObject pdu = JsonNode.Parse(json)!.AsObject();
            if (pdu["content"] is not JsonObject content || content["via"] is not JsonArray { Count: > 0 } || pdu["state_key"]?.GetValue<string>() is not string child || !child.StartsWith('!')) continue;
            if (suggestedOnly && content["suggested"]?.GetValue<bool>() != true) continue;
            children.Add(new JsonObject { ["type"] = "m.space.child", ["state_key"] = child, ["sender"] = pdu["sender"]?.DeepClone(), ["origin_server_ts"] = pdu["origin_server_ts"]?.DeepClone(), ["content"] = content.DeepClone() });
        }
        // Return ordered by their order, then state key
        return [.. children.OrderBy(c => c["content"]?["order"] is JsonValue v && v.TryGetValue(out string? order) ? order : "\uffff", StringComparer.Ordinal).ThenBy(c => c["state_key"]!.GetValue<string>(), StringComparer.Ordinal)];
    }

    /// <summary>Return the content of a current state event, or null.</summary>
    private static async Task<JsonObject?> StateContentAsync(ServerDbContext dbContext, string roomId, string type, string stateKey, CancellationToken cancellationToken)
    {
        // State row, then its event
        string? id = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == type && s.StateKey == stateKey).Select(s => s.EventId).SingleOrDefaultAsync(cancellationToken);
        string? json = id is null ? null : await dbContext.Events.AsNoTracking().Where(e => e.EventId == id).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        // Return content
        return json is null ? null : JsonNode.Parse(json)?["content"] as JsonObject;
    }

    /// <summary>Search users who share a room with the requester or are in a public room, by user ID or display name.</summary>
    public async Task<JsonObject> SearchUsersAsync(string userId, string term, int limit, CancellationToken cancellationToken)
    {
        // Rooms that make users visible: the requester's and the public ones
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> ownRooms = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
        List<string> publicRooms = await db.Rooms.AsNoTracking().Where(r => r.Published).Select(r => r.RoomId).ToListAsync(cancellationToken);
        List<string> visibleRooms = [.. ownRooms.Union(publicRooms)];
        List<string> candidates = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && visibleRooms.Contains(s.RoomId)).Select(s => s.StateKey).Distinct().ToListAsync(cancellationToken);

        // Every word of the term in the ID or the display name
        Dictionary<string, ProfileRow> profiles = await db.Profiles.AsNoTracking().Where(p => candidates.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);
        string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> matches = [.. candidates
            .Where(c => words.All(w => c.Contains(w, StringComparison.OrdinalIgnoreCase) || profiles.GetValueOrDefault(c)?.DisplayName?.Contains(w, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(c => c, StringComparer.Ordinal)];

        // Results up to the limit
        JsonArray results = [];
        foreach (string match in matches.Take(limit))
        {
            JsonObject result = new() { ["user_id"] = match };
            if (profiles.GetValueOrDefault(match)?.DisplayName is string name) result["display_name"] = name;
            if (profiles.GetValueOrDefault(match)?.AvatarUrl is string avatar) result["avatar_url"] = avatar;
            results.Add(result);
        }
        // Return results
        return new JsonObject { ["results"] = results, ["limited"] = matches.Count > limit };
    }

    /// <summary>Build a directory entry from a room's current state.</summary>
    private static async Task<JsonObject> PublicEntryAsync(ServerDbContext dbContext, string roomId, CancellationToken cancellationToken)
    {
        // Contents of the state the directory shows
        string[] types = [EventTypes.RoomCreate, EventTypes.RoomName, EventTypes.RoomTopic, EventTypes.RoomCanonicalAlias, "m.room.avatar", "m.room.join_rules", "m.room.history_visibility", "m.room.guest_access"];
        List<string> ids = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.StateKey == "" && types.Contains(s.Type)).Select(s => s.EventId).ToListAsync(cancellationToken);
        Dictionary<string, JsonObject?> content = (await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).Select(e => new { e.Type, e.Json }).ToListAsync(cancellationToken))
            .ToDictionary(e => e.Type, e => JsonNode.Parse(e.Json)!["content"] as JsonObject);
        string? Field(string type, string key) => content.GetValueOrDefault(type)?[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        // Entry with the optional fields that are set
        JsonObject entry = new()
        {
            ["room_id"] = roomId,
            ["num_joined_members"] = await dbContext.CurrentState.CountAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join, cancellationToken),
            ["world_readable"] = Field("m.room.history_visibility", "history_visibility") == "world_readable",
            ["guest_can_join"] = Field("m.room.guest_access", "guest_access") == "can_join",
            ["join_rule"] = Field("m.room.join_rules", "join_rule") ?? "invite",
        };
        foreach ((string key, string? value) in new[] { ("name", Field(EventTypes.RoomName, "name")), ("topic", Field(EventTypes.RoomTopic, "topic")), ("canonical_alias", Field(EventTypes.RoomCanonicalAlias, "alias")), ("avatar_url", Field("m.room.avatar", "url")), ("room_type", Field(EventTypes.RoomCreate, "type")) })
        {
            if (value is not null) entry[key] = value;
        }
        // Return entry
        return entry;
    }

    /// <summary>Throw <c>M_INVALID_PARAM</c> unless <paramref name="alias"/> is a well-formed alias of this server, which it can create or delete.</summary>
    private void AssertLocalAlias(string alias)
    {
        // Well formed, then local
        if (AssertAlias(alias) != ServerName) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Aliases of other servers are managed on their server.");
    }

    /// <summary>Return the server of <paramref name="alias"/>; throw <c>M_INVALID_PARAM</c> unless it is a well-formed alias.</summary>
    private static string AssertAlias(string alias)
    {
        // #localpart:server
        int colon = alias.IndexOf(':');
        if (!alias.StartsWith('#') || colon < 2 || alias.Length > MaxAliasLength || alias.AsSpan(1, colon - 1).ContainsAny(":# ")) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{alias} is not a valid room alias.");
        // Return server part
        return alias[(colon + 1)..];
    }

    /// <summary>Return whether the user administers the server.</summary>
    private static Task<bool> IsAdminAsync(ServerDbContext dbContext, string userId, CancellationToken cancellationToken) => dbContext.Users.AnyAsync(u => u.UserId == userId && u.Admin, cancellationToken);
}
