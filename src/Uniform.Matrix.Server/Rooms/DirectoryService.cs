namespace Uniform.Matrix.Server.Rooms;

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#room-aliases">Room aliases</see>, the <see href="https://spec.matrix.org/latest/client-server-api/#listing-rooms">public room directory</see> and <see href="https://spec.matrix.org/latest/client-server-api/#user-directory">user search</see>.</summary>
public sealed class DirectoryService
{
    private const int MaxAliasLength = 255;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly RoomService rooms;
    private readonly IOptions<ServerOptions> options;

    /// <summary>Create the service.</summary>
    public DirectoryService(IDbContextFactory<ServerDbContext> contexts, RoomService rooms, IOptions<ServerOptions> options)
    {
        // Store dependencies
        this.contexts = contexts;
        this.rooms = rooms;
        this.options = options;
    }

    /// <summary>Server name of local aliases.</summary>
    private string ServerName => options.Value.ServerName;

    /// <summary>Return the room of a local alias and the servers that know it; throw <c>M_NOT_FOUND</c> for unknown aliases.</summary>
    public async Task<JsonObject> GetAliasAsync(string alias, CancellationToken cancellationToken)
    {
        // Local aliases only
        AssertLocalAlias(alias);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomAliasRow row = await db.RoomAliases.AsNoTracking().SingleOrDefaultAsync(a => a.Alias == alias, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"Room alias {alias} not found.");
        // Return room and server
        return new JsonObject { ["room_id"] = row.RoomId, ["servers"] = new JsonArray(ServerName) };
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
        // Assert local directory and page token
        if (server is not null && server != ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Listing other servers' rooms needs federation, which is not supported yet.");
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

    /// <summary>Throw <c>M_INVALID_PARAM</c> unless <paramref name="alias"/> is a well-formed alias of this server.</summary>
    private void AssertLocalAlias(string alias)
    {
        // #localpart:server
        int colon = alias.IndexOf(':');
        if (!alias.StartsWith('#') || colon < 2 || alias.Length > MaxAliasLength || alias.AsSpan(1, colon - 1).ContainsAny(":# ")) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{alias} is not a valid room alias.");
        if (alias[(colon + 1)..] != ServerName) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Aliases of other servers need federation, which is not supported yet.");
    }

    /// <summary>Return whether the user administers the server.</summary>
    private static Task<bool> IsAdminAsync(ServerDbContext dbContext, string userId, CancellationToken cancellationToken) => dbContext.Users.AnyAsync(u => u.UserId == userId && u.Admin, cancellationToken);
}
