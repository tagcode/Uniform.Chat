namespace Uniform.Matrix.Server.Rooms;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Storage;

/// <summary>Rooms of local users: creation, membership, messages and state, redaction, and what a user may read.</summary>
public sealed class RoomService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly EventSender sender;
    private readonly IOptions<ServerOptions> options;
    private readonly TimeProvider time;
    private readonly RemoteJoins remoteJoins;
    private readonly RemoteInvites remoteInvites;
    private readonly ILogger<RoomService> logger;

    /// <summary>Create the service.</summary>
    public RoomService(IDbContextFactory<ServerDbContext> contexts, EventSender sender, IOptions<ServerOptions> options, TimeProvider time, RemoteJoins remoteJoins, RemoteInvites remoteInvites, ILogger<RoomService> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.sender = sender;
        this.options = options;
        this.time = time;
        this.remoteJoins = remoteJoins;
        this.remoteInvites = remoteInvites;
        this.logger = logger;
    }

    /// <summary>Server name of local rooms and users.</summary>
    private string ServerName => options.Value.ServerName;

    /// <summary>Create a room following <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3createroom">createRoom</see>; return its ID.</summary>
    public async Task<string> CreateRoomAsync(Requester requester, JsonObject body, CancellationToken cancellationToken)
    {
        // Version, preset and alias
        string versionId = body["room_version"]?.GetValue<string>() ?? RoomVersion.Default.Id;
        RoomVersion version = RoomVersion.Find(versionId) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UnsupportedRoomVersion, $"Room version {versionId} is not supported.");
        bool published = body["visibility"]?.GetValue<string>() == "public";
        string preset = body["preset"]?.GetValue<string>() ?? (published ? "public_chat" : "private_chat");
        if (preset is not ("private_chat" or "public_chat" or "trusted_private_chat")) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown preset {preset}.");
        string? alias = body["room_alias_name"]?.GetValue<string>() is string aliasName ? $"#{aliasName}:{ServerName}" : null;
        List<string> invites = [.. (body["invite"] as JsonArray ?? []).Select(i => i!.GetValue<string>())];

        // Create content; from version 12 the room ID is its hash, so the room is stored with its create event
        JsonObject create = body["creation_content"]?.DeepClone() as JsonObject ?? [];
        create["room_version"] = version.Id;
        if (version.CreateHasCreator) create["creator"] = requester.UserId;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (alias is not null && await db.RoomAliases.AnyAsync(a => a.Alias == alias, cancellationToken)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.RoomInUse, "The room alias is already taken.");
        }
        string roomId;
        if (version.RoomIdIsCreateHash) roomId = await sender.CreateHashedRoomAsync(version, requester.UserId, create, published, cancellationToken);
        else
        {
            roomId = $"!{string.Concat(RandomNumberGenerator.GetItems<char>("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 18))}:{ServerName}";
            await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
            {
                db.Rooms.Add(new RoomRow { RoomId = roomId, Version = version.Id, Creator = requester.UserId, CreatedTs = time.GetUtcNow().ToUnixTimeMilliseconds(), Published = published });
                await db.SaveChangesAsync(cancellationToken);
            }
            await SendStateAsync(roomId, requester.UserId, EventTypes.RoomCreate, "", create, cancellationToken);
        }

        // Alias, then join and power levels
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (alias is not null && await db.RoomAliases.AnyAsync(a => a.Alias == alias, cancellationToken)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.RoomInUse, "The room alias is already taken.");
            if (alias is not null) db.RoomAliases.Add(new RoomAliasRow { Alias = alias, RoomId = roomId, Creator = requester.UserId });
            await db.SaveChangesAsync(cancellationToken);
        }
        await SendStateAsync(roomId, requester.UserId, EventTypes.RoomMember, requester.UserId, await MemberContentAsync(requester.UserId, Memberships.Join, null, cancellationToken), cancellationToken);
        await SendStateAsync(roomId, requester.UserId, EventTypes.RoomPowerLevels, "", PowerLevelsContent(requester.UserId, version.RoomIdIsCreateHash, preset, invites, body["power_level_content_override"] as JsonObject), cancellationToken);
        if (alias is not null) await SendStateAsync(roomId, requester.UserId, EventTypes.RoomCanonicalAlias, "", new JsonObject { ["alias"] = alias }, cancellationToken);

        // Preset state
        await SendStateAsync(roomId, requester.UserId, "m.room.join_rules", "", new JsonObject { ["join_rule"] = preset == "public_chat" ? "public" : "invite" }, cancellationToken);
        await SendStateAsync(roomId, requester.UserId, "m.room.history_visibility", "", new JsonObject { ["history_visibility"] = "shared" }, cancellationToken);
        await SendStateAsync(roomId, requester.UserId, "m.room.guest_access", "", new JsonObject { ["guest_access"] = preset == "public_chat" ? "forbidden" : "can_join" }, cancellationToken);

        // Initial state, then name and topic
        foreach (JsonNode? state in body["initial_state"] as JsonArray ?? [])
        {
            if (state?["type"]?.GetValue<string>() is not string type || state["content"] is not JsonObject content) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidRoomState, "Initial state events need a type and content.");
            await SendStateAsync(roomId, requester.UserId, type, state["state_key"]?.GetValue<string>() ?? "", (JsonObject)content.DeepClone(), cancellationToken);
        }
        if (body["name"]?.GetValue<string>() is string name) await SendStateAsync(roomId, requester.UserId, EventTypes.RoomName, "", new JsonObject { ["name"] = name }, cancellationToken);
        if (body["topic"]?.GetValue<string>() is string topic) await SendStateAsync(roomId, requester.UserId, EventTypes.RoomTopic, "", new JsonObject { ["topic"] = topic }, cancellationToken);

        // Invites
        bool direct = body["is_direct"]?.GetValue<bool>() == true;
        foreach (string invitee in invites) await InviteAsync(requester.UserId, roomId, invitee, null, direct, cancellationToken);
        // Return room
        return roomId;
    }

    /// <summary>Join a room by ID or alias; already joined users stay joined. Aliases of other servers are asked from them. A room ID that is not here yet is joined through the server that invited the user, <paramref name="via"/> servers and the server in the room ID, in that order. Returns the room ID.</summary>
    public async Task<string> JoinAsync(string userId, string roomIdOrAlias, string? reason, CancellationToken cancellationToken, IReadOnlyList<string>? via = null)
    {
        // Aliases of other servers to their room and its servers
        if (roomIdOrAlias.StartsWith('#') && roomIdOrAlias.IndexOf(':') is int aliasColon and > 1 && roomIdOrAlias[(aliasColon + 1)..] != ServerName)
        {
            (string aliasRoomId, IReadOnlyList<string> aliasServers) = await remoteJoins.ResolveAliasAsync(roomIdOrAlias, roomIdOrAlias[(aliasColon + 1)..], cancellationToken);
            return await JoinAsync(userId, aliasRoomId, reason, cancellationToken, [.. aliasServers, .. via ?? []]);
        }

        // Rooms not here through other servers; the first that answers decides
        string? inviting = roomIdOrAlias.StartsWith('!') ? await remoteInvites.GetInvitingServerAsync(userId, roomIdOrAlias, cancellationToken) : null;
        List<string> residents = [.. new[] { inviting }.Concat(via ?? []).Append(RoomServer(roomIdOrAlias)).OfType<string>().Where(s => s != ServerName).Distinct()];
        if (roomIdOrAlias.StartsWith('!') && residents.Count > 0 && !await ExistsAsync(roomIdOrAlias, cancellationToken))
        {
            JsonObject content = await MemberContentAsync(userId, Memberships.Join, reason, cancellationToken);
            for (int i = 0; ; i++)
            {
                try
                {
                    await remoteJoins.JoinAsync(userId, roomIdOrAlias, residents[i], content, cancellationToken);
                    await remoteInvites.EndAsync(userId, roomIdOrAlias, joined: true, cancellationToken);
                    return roomIdOrAlias;
                }
                catch (MatrixException) when (i < residents.Count - 1)
                {
                    // Next server
                }
            }
        }

        // Resolve, then join unless joined
        string roomId = await ResolveAsync(roomIdOrAlias, cancellationToken);
        if (await GetMembershipAsync(roomId, userId, cancellationToken) == Memberships.Join) return roomId;
        await SendStateAsync(roomId, userId, EventTypes.RoomMember, userId, await MemberContentAsync(userId, Memberships.Join, reason, cancellationToken), cancellationToken);
        await remoteInvites.EndAsync(userId, roomId, joined: true, cancellationToken);
        // Return room
        return roomId;
    }

    /// <summary>Invite a user: local users directly, other servers' users through their server, which signs the invite too.</summary>
    public async Task InviteAsync(string userId, string roomId, string target, string? reason, bool isDirect, CancellationToken cancellationToken)
    {
        // Other servers' users through their server
        if (!UserId.TryParse(target, out UserId invitee)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Invalid user ID.");
        if (invitee.ServerName != ServerName)
        {
            JsonObject remote = new() { ["membership"] = Memberships.Invite };
            if (reason is not null) remote["reason"] = reason;
            if (isDirect) remote["is_direct"] = true;
            await remoteInvites.InviteAsync(userId, roomId, target, invitee.ServerName, remote, cancellationToken);
            return;
        }

        // Local, existing users
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Users.AnyAsync(u => u.UserId == target, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown user.");
        }

        // Invite with the target's profile
        JsonObject content = await MemberContentAsync(target, Memberships.Invite, reason, cancellationToken);
        if (isDirect) content["is_direct"] = true;
        await SendStateAsync(roomId, userId, EventTypes.RoomMember, target, content, cancellationToken);
    }

    /// <summary>Change another user's membership: kick (<c>leave</c>), <c>ban</c> or unban (<c>leave</c> of a banned user).</summary>
    public Task SetMembershipAsync(string userId, string roomId, string target, string membership, string? reason, CancellationToken cancellationToken)
    {
        // Content with an optional reason
        JsonObject content = new() { ["membership"] = membership };
        if (reason is not null) content["reason"] = reason;
        // Done
        return SendStateAsync(roomId, userId, EventTypes.RoomMember, target, content, cancellationToken);
    }

    /// <summary>Leave a room; leaving a room one is not in does nothing.</summary>
    public async Task LeaveAsync(string userId, string roomId, string? reason, CancellationToken cancellationToken)
    {
        // An invite from another server to a room not here is rejected through that server; a server that cannot be reached is not waited for
        if (!await ExistsAsync(roomId, cancellationToken) && await remoteInvites.GetInvitingServerAsync(userId, roomId, cancellationToken) is string inviting)
        {
            try
            {
                await remoteJoins.RejectInviteAsync(userId, roomId, inviting, reason, cancellationToken);
            }
            catch (MatrixException ex)
            {
                logger.LogInformation("Rejecting the invite of {UserId} to {RoomId} could not reach {Server}: {Error}", userId, roomId, inviting, ex.Message);
            }
            await remoteInvites.EndAsync(userId, roomId, joined: false, cancellationToken);
            return;
        }

        // Only from invite, join or knock
        string? membership = await GetMembershipAsync(roomId, userId, cancellationToken);
        if (membership is null or Memberships.Leave or Memberships.Ban) return;
        await SetMembershipAsync(userId, roomId, userId, Memberships.Leave, reason, cancellationToken);
        await remoteInvites.EndAsync(userId, roomId, joined: true, cancellationToken);
    }

    /// <summary>Forget a room the user has left, so it disappears from their sync.</summary>
    public async Task ForgetAsync(string userId, string roomId, CancellationToken cancellationToken)
    {
        // Not while joined
        if (await GetMembershipAsync(roomId, userId, cancellationToken) is Memberships.Join or Memberships.Invite) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, "Leave the room before forgetting it.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.ForgottenRooms.AnyAsync(f => f.UserId == userId && f.RoomId == roomId, cancellationToken)) db.ForgottenRooms.Add(new ForgottenRoomRow { UserId = userId, RoomId = roomId });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Send a message event; return its ID, the same for a retried transaction.</summary>
    public Task<string> SendMessageAsync(Requester requester, string roomId, string type, JsonObject content, string transactionId, CancellationToken cancellationToken) =>
        sender.SendAsync(new EventDraft { RoomId = roomId, Sender = requester.UserId, Type = type, Content = content, DeviceId = requester.DeviceId, TransactionId = transactionId }, cancellationToken);

    /// <summary>Set room state; return the event ID.</summary>
    public Task<string> SetStateAsync(Requester requester, string roomId, string type, string stateKey, JsonObject content, CancellationToken cancellationToken) =>
        SendStateAsync(roomId, requester.UserId, type, stateKey, content, cancellationToken);

    /// <summary>Redact an event of the room: one's own, or anyone's with the redact level. Returns the redaction's ID.</summary>
    public async Task<string> RedactAsync(Requester requester, string roomId, string eventId, string? reason, string transactionId, CancellationToken cancellationToken)
    {
        // Target in this room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        EventRow target = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId && e.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown event.");
        RoomRow room = await db.Rooms.AsNoTracking().SingleAsync(r => r.RoomId == roomId, cancellationToken);

        // Own events, or the redact level
        if (target.Sender != requester.UserId)
        {
            PowerLevels levels = await GetPowerLevelsAsync(db, room, cancellationToken);
            if (levels.UserLevel(requester.UserId) < levels.Redact) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not allowed to redact others' events.");
        }

        // Version 11 names the target in content, older versions at the top level
        JsonObject content = [];
        if (reason is not null) content["reason"] = reason;
        RoomVersion version = RoomVersion.Find(room.Version)!;
        if (version.Redaction == RedactionRules.V11) content["redacts"] = eventId;
        // Return redaction ID
        return await sender.SendAsync(new EventDraft { RoomId = roomId, Sender = requester.UserId, Type = EventTypes.RoomRedaction, Content = content, Redacts = eventId, DeviceId = requester.DeviceId, TransactionId = transactionId }, cancellationToken);
    }

    /// <summary>Update the user's member event in every joined room to their current profile.</summary>
    public async Task UpdateMemberProfilesAsync(string userId, CancellationToken cancellationToken)
    {
        // Rejoin with the new profile, keeping the membership
        foreach (string roomId in await GetJoinedRoomsAsync(userId, cancellationToken))
        {
            await SendStateAsync(roomId, userId, EventTypes.RoomMember, userId, await MemberContentAsync(userId, Memberships.Join, null, cancellationToken), cancellationToken);
        }
    }

    /// <summary>Return whether a joined user's power level allows sending state of <paramref name="type"/>.</summary>
    public async Task<bool> CanSendStateAsync(string userId, string roomId, string type, CancellationToken cancellationToken)
    {
        // Joined, and at the event's level
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RoomRow? room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken);
        if (room is null) return false;
        if (!await db.CurrentState.AnyAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join, cancellationToken)) return false;
        PowerLevels levels = await GetPowerLevelsAsync(db, room, cancellationToken);
        // Return verdict
        return levels.UserLevel(userId) >= levels.EventLevel(type, isState: true);
    }

    /// <summary>Return the server of a room ID, e.g. <c>b.org</c> of <c>!abc:b.org</c>; null for aliases and malformed IDs.</summary>
    private static string? RoomServer(string roomIdOrAlias) => roomIdOrAlias.StartsWith('!') && roomIdOrAlias.IndexOf(':') is int colon and > 1 ? roomIdOrAlias[(colon + 1)..] : null;

    /// <summary>Return whether the room is on this server.</summary>
    private async Task<bool> ExistsAsync(string roomId, CancellationToken cancellationToken)
    {
        // Any row
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return whether found
        return await db.Rooms.AnyAsync(r => r.RoomId == roomId, cancellationToken);
    }

    /// <summary>Return the room ID of a room ID or local alias; throw <c>M_NOT_FOUND</c> for unknown rooms.</summary>
    public async Task<string> ResolveAsync(string roomIdOrAlias, CancellationToken cancellationToken)
    {
        // Aliases map to rooms
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string roomId = roomIdOrAlias;
        if (roomIdOrAlias.StartsWith('#')) roomId = (await db.RoomAliases.AsNoTracking().SingleOrDefaultAsync(a => a.Alias == roomIdOrAlias, cancellationToken))?.RoomId ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room alias.");
        if (!await db.Rooms.AnyAsync(r => r.RoomId == roomId, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room; rooms on other servers need federation, which is not supported yet.");
        // Return room ID
        return roomId;
    }

    /// <summary>Return a user's current membership in a room, or null.</summary>
    public async Task<string?> GetMembershipAsync(string roomId, string userId, CancellationToken cancellationToken)
    {
        // Current state row
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return membership
        return (await db.CurrentState.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId, cancellationToken))?.Membership;
    }

    /// <summary>Return the rooms a user is joined to.</summary>
    public async Task<IReadOnlyList<string>> GetJoinedRoomsAsync(string userId, CancellationToken cancellationToken)
    {
        // Membership index
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return room IDs
        return await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
    }

    /// <summary>Return the state a user may see: the current state while joined, otherwise the state when they left; throw <c>M_FORBIDDEN</c> for others.</summary>
    public async Task<IReadOnlyList<JsonObject>> GetStateAsync(Requester requester, string roomId, CancellationToken cancellationToken)
    {
        // Visible state
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        Dictionary<(string Type, string StateKey), string> state = await GetVisibleStateAsync(db, requester.UserId, roomId, cancellationToken);
        // Return events
        return await LoadClientEventsAsync(db, requester, state.Values, cancellationToken);
    }

    /// <summary>Return the content of one state event a user may see; throw <c>M_NOT_FOUND</c> when unset.</summary>
    public async Task<JsonObject> GetStateContentAsync(Requester requester, string roomId, string type, string stateKey, CancellationToken cancellationToken)
    {
        // Visible state entry
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        Dictionary<(string Type, string StateKey), string> state = await GetVisibleStateAsync(db, requester.UserId, roomId, cancellationToken);
        if (!state.TryGetValue((type, stateKey), out string? eventId)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Event not found.");
        EventRow row = await db.Events.AsNoTracking().SingleAsync(e => e.EventId == eventId, cancellationToken);
        // Return content
        return JsonNode.Parse(row.Json)!["content"]?.AsObject().DeepClone().AsObject() ?? [];
    }

    /// <summary>Return an event a user may see: anything up to their leaving, while they were or are a member.</summary>
    public async Task<JsonObject> GetEventAsync(Requester requester, string roomId, string eventId, CancellationToken cancellationToken)
    {
        // Event of this room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        EventRow row = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId && e.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Event not found.");

        // Up to the user's own membership end
        long? until = await GetVisibleUntilAsync(db, requester.UserId, roomId, cancellationToken);
        if (until is null || row.Stream > until) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Event not found.");
        // Return client event
        return (await LoadClientEventsAsync(db, requester, [row.EventId], cancellationToken)).Single();
    }

    /// <summary>Return the joined members with display names and avatars; only for members.</summary>
    public async Task<JsonObject> GetJoinedMembersAsync(Requester requester, string roomId, CancellationToken cancellationToken)
    {
        // Members only
        if (await GetMembershipAsync(roomId, requester.UserId, cancellationToken) != Memberships.Join) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not a member of the room.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> ids = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.EventId).ToListAsync(cancellationToken);
        List<EventRow> rows = await db.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).ToListAsync(cancellationToken);

        // Name and avatar from each join
        JsonObject joined = [];
        foreach (EventRow row in rows)
        {
            JsonNode? content = JsonNode.Parse(row.Json)!["content"];
            joined[row.StateKey!] = new JsonObject { ["display_name"] = content?["displayname"]?.DeepClone(), ["avatar_url"] = content?["avatar_url"]?.DeepClone() };
        }
        // Return members
        return new JsonObject { ["joined"] = joined };
    }

    /// <summary>Send a state event without a transaction.</summary>
    private Task<string> SendStateAsync(string roomId, string userId, string type, string stateKey, JsonObject content, CancellationToken cancellationToken) =>
        sender.SendAsync(new EventDraft { RoomId = roomId, Sender = userId, Type = type, StateKey = stateKey, Content = content }, cancellationToken);

    /// <summary>Return membership content with the user's display name and avatar.</summary>
    private async Task<JsonObject> MemberContentAsync(string userId, string membership, string? reason, CancellationToken cancellationToken)
    {
        // Profile of local users
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        ProfileRow? profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        JsonObject content = new() { ["membership"] = membership };
        if (profile?.DisplayName is string name) content["displayname"] = name;
        if (profile?.AvatarUrl is string avatar) content["avatar_url"] = avatar;
        if (reason is not null) content["reason"] = reason;
        // Return content
        return content;
    }

    /// <summary>Return the default power levels of a new room, with the client's overrides.</summary>
    private static JsonObject PowerLevelsContent(string creator, bool creatorAboveLevels, string preset, IReadOnlyList<string> invites, JsonObject? overrides)
    {
        // Creator at 100, unless creators outrank levels as from version 12; invitees too in trusted private chats
        JsonObject users = creatorAboveLevels ? [] : new JsonObject { [creator] = 100 };
        if (preset == "trusted_private_chat") foreach (string invitee in invites) users[invitee] = 100;
        JsonObject content = new()
        {
            ["users"] = users,
            ["users_default"] = 0,
            ["events"] = new JsonObject { ["m.room.name"] = 50, ["m.room.power_levels"] = 100, ["m.room.history_visibility"] = 100, ["m.room.canonical_alias"] = 50, ["m.room.avatar"] = 50, ["m.room.tombstone"] = 100, ["m.room.server_acl"] = 100, ["m.room.encryption"] = 100 },
            ["events_default"] = 0,
            ["state_default"] = 50,
            ["ban"] = 50,
            ["kick"] = 50,
            ["redact"] = 50,
            ["invite"] = 0,
        };

        // Overrides replace top-level keys
        foreach ((string key, JsonNode? value) in overrides ?? []) content[key] = value?.DeepClone();
        // Return content
        return content;
    }

    /// <summary>Return the room's power levels from its current state, with the creators of version 12 rooms above them.</summary>
    private static async Task<PowerLevels> GetPowerLevelsAsync(ServerDbContext dbContext, RoomRow room, CancellationToken cancellationToken)
    {
        // Current power levels event, if any
        JsonObject? content = await CurrentContentAsync(dbContext, room.RoomId, EventTypes.RoomPowerLevels, cancellationToken);
        IReadOnlyCollection<string>? creators = null;
        if (RoomVersion.Find(room.Version)?.RoomIdIsCreateHash == true && await CurrentEventAsync(dbContext, room.RoomId, EventTypes.RoomCreate, cancellationToken) is JsonObject create) creators = AuthRules.Creators(create);
        // Return levels
        return new PowerLevels(content, room.Creator, creators);
    }

    /// <summary>Return the current state event of a type with an empty state key, or null.</summary>
    private static async Task<JsonObject?> CurrentEventAsync(ServerDbContext dbContext, string roomId, string type, CancellationToken cancellationToken)
    {
        // State row, then its event
        string? id = (await dbContext.CurrentState.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == type && s.StateKey == "", cancellationToken))?.EventId;
        // Return event
        return id is null ? null : JsonNode.Parse((await dbContext.Events.AsNoTracking().SingleAsync(e => e.EventId == id, cancellationToken)).Json) as JsonObject;
    }

    /// <summary>Return the content of the current state event of a type with an empty state key, or null.</summary>
    private static async Task<JsonObject?> CurrentContentAsync(ServerDbContext dbContext, string roomId, string type, CancellationToken cancellationToken) =>
        (await CurrentEventAsync(dbContext, roomId, type, cancellationToken))?["content"] as JsonObject;

    /// <summary>Return the state a user may see, as event IDs; throw <c>M_FORBIDDEN</c> when they never were a member.</summary>
    private static async Task<Dictionary<(string Type, string StateKey), string>> GetVisibleStateAsync(ServerDbContext dbContext, string userId, string roomId, CancellationToken cancellationToken)
    {
        // Current state while joined
        CurrentStateRow? member = await dbContext.CurrentState.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId, cancellationToken);
        RoomRow room = await dbContext.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.RoomId == roomId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        if (member?.Membership == Memberships.Join) return await StateGroups.LoadAsync(dbContext, room.CurrentStateGroup, cancellationToken);

        // State at leaving for former members
        if (member?.Membership is not (Memberships.Leave or Memberships.Ban)) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not a member of the room.");
        long group = (await dbContext.Events.AsNoTracking().SingleAsync(e => e.EventId == member.EventId, cancellationToken)).StateGroup;
        // Return state at leaving
        return await StateGroups.LoadAsync(dbContext, group, cancellationToken);
    }

    /// <summary>Return the last stream position a user may read in a room: everything while joined, up to leaving afterwards; null for non-members.</summary>
    private static async Task<long?> GetVisibleUntilAsync(ServerDbContext dbContext, string userId, string roomId, CancellationToken cancellationToken)
    {
        // Membership now
        CurrentStateRow? member = await dbContext.CurrentState.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId, cancellationToken);
        // Return limit
        return member?.Membership switch
        {
            Memberships.Join => long.MaxValue,
            Memberships.Leave or Memberships.Ban => member.Stream,
            _ => null,
        };
    }

    /// <summary>Load events in client format, with transaction IDs for the requester's device and redaction reasons.</summary>
    private async Task<IReadOnlyList<JsonObject>> LoadClientEventsAsync(ServerDbContext dbContext, Requester requester, IEnumerable<string> eventIds, CancellationToken cancellationToken)
    {
        // Events and their redactions
        List<string> ids = [.. eventIds];
        List<EventRow> rows = await dbContext.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).OrderBy(e => e.Stream).ToListAsync(cancellationToken);
        List<string> redactionIds = [.. rows.Select(r => r.RedactedBy).OfType<string>()];
        Dictionary<string, EventRow> redactions = await dbContext.Events.AsNoTracking().Where(e => redactionIds.Contains(e.EventId)).ToDictionaryAsync(e => e.EventId, cancellationToken);
        Dictionary<string, string> transactions = await dbContext.Transactions.AsNoTracking().Where(t => t.UserId == requester.UserId && t.DeviceId == requester.DeviceId && ids.Contains(t.EventId)).ToDictionaryAsync(t => t.EventId, t => t.TransactionId, cancellationToken);

        // Client form
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        // Return events
        return [.. rows.Select(r => ClientFormat.ToClient(r, now, transactions.GetValueOrDefault(r.EventId), r.RedactedBy is string by && redactions.TryGetValue(by, out EventRow? redaction) ? ClientFormat.ToClient(redaction, now) : null))];
    }
}
