namespace Uniform.Matrix.Protocol.Client;

using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

// Room membership, creation, aliases, state and members.
public sealed partial class MatrixClient
{
    /// <summary>List <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3joined_rooms">joined room</see> IDs.</summary>
    public async Task<IReadOnlyList<string>> GetJoinedRoomsAsync(CancellationToken cancellationToken = default)
    {
        // Fetch
        JoinedRoomsResponse response = await SendAsync<JoinedRoomsResponse>(HttpMethod.Get, "_matrix/client/v3/joined_rooms", body: null, authenticated: true, cancellationToken);
        // Return room IDs
        return response.JoinedRooms;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3joinroomidoralias">Joins</see> a room by ID or alias, accepting an invite if there is one; returns the room ID.</summary>
    /// <param name="roomIdOrAlias">Room ID like <c>!abc:server</c> or alias like <c>#room:server</c>.</param>
    /// <param name="via">Servers to join through when this server is not in the room.</param>
    /// <param name="reason">Reason shown in the membership event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> JoinRoomAsync(string roomIdOrAlias, IEnumerable<string>? via = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        // Build query; server_name is the pre-v1.12 name of via
        List<string> servers = via?.ToList() ?? [];
        string path = WithQuery($"_matrix/client/v3/join/{Escape(roomIdOrAlias)}", servers.Select(s => ("via", (string?)s)).Concat(servers.Select(s => ("server_name", (string?)s))));
        // Join
        RoomIdResponse response = await SendAsync<RoomIdResponse>(HttpMethod.Post, path, new { reason }, authenticated: true, cancellationToken);
        // Return room ID
        return response.RoomId;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidleave">Leaves</see> a room or rejects an invite.</summary>
    public Task LeaveRoomAsync(string roomId, string? reason = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/leave", new { reason }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidforget">Forgets</see> a left room so it no longer appears in sync.</summary>
    public Task ForgetRoomAsync(string roomId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/forget", new { }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidinvite">Invites</see> <paramref name="userId"/> to a room.</summary>
    public Task InviteAsync(string roomId, string userId, string? reason = null, CancellationToken cancellationToken = default) => SendMembershipAsync(roomId, "invite", userId, reason, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidkick">Kicks</see> <paramref name="userId"/> from a room.</summary>
    public Task KickAsync(string roomId, string userId, string? reason = null, CancellationToken cancellationToken = default) => SendMembershipAsync(roomId, "kick", userId, reason, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidban">Bans</see> <paramref name="userId"/> from a room.</summary>
    public Task BanAsync(string roomId, string userId, string? reason = null, CancellationToken cancellationToken = default) => SendMembershipAsync(roomId, "ban", userId, reason, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidunban">Unbans</see> <paramref name="userId"/>.</summary>
    public Task UnbanAsync(string roomId, string userId, string? reason = null, CancellationToken cancellationToken = default) => SendMembershipAsync(roomId, "unban", userId, reason, cancellationToken);

    /// <summary>Post <paramref name="action"/> (invite, kick, ban or unban) for <paramref name="userId"/>.</summary>
    private Task SendMembershipAsync(string roomId, string action, string userId, string? reason, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/{action}", new { user_id = userId, reason }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3createroom">Creates</see> a room and returns its ID.</summary>
    public async Task<string> CreateRoomAsync(CreateRoomRequest request, CancellationToken cancellationToken = default)
    {
        // Create
        RoomIdResponse response = await SendAsync<RoomIdResponse>(HttpMethod.Post, "_matrix/client/v3/createRoom", request, authenticated: true, cancellationToken);
        // Return room ID
        return response.RoomId;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3directoryroomroomalias">Resolves</see> an alias like <c>#room:server</c> to a room ID.</summary>
    public Task<RoomAliasResponse> ResolveRoomAliasAsync(string roomAlias, CancellationToken cancellationToken = default) =>
        SendAsync<RoomAliasResponse>(HttpMethod.Get, $"_matrix/client/v3/directory/room/{Escape(roomAlias)}", body: null, authenticated: true, cancellationToken);

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidstate">all current state</see> events of a room.</summary>
    public Task<IReadOnlyList<ClientEvent>> GetStateAsync(string roomId, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ClientEvent>>(HttpMethod.Get, $"_matrix/client/v3/rooms/{Escape(roomId)}/state", body: null, authenticated: true, cancellationToken);

    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidstateeventtypestatekey">content of one state event</see>; throw <see cref="MatrixException"/> with <see cref="ErrorCodes.NotFound"/> when unset.</summary>
    /// <example><code><![CDATA[TopicContent topic = await client.GetStateEventAsync<TopicContent>(roomId, EventTypes.RoomTopic);]]></code></example>
    public Task<T> GetStateEventAsync<T>(string roomId, string eventType, string stateKey = "", CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, $"_matrix/client/v3/rooms/{Escape(roomId)}/state/{Escape(eventType)}/{Escape(stateKey)}", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3roomsroomidstateeventtypestatekey">Sets</see> a state event and returns its event ID.</summary>
    public async Task<string> SendStateEventAsync(string roomId, string eventType, object content, string stateKey = "", CancellationToken cancellationToken = default)
    {
        // Send
        EventIdResponse response = await SendAsync<EventIdResponse>(HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/state/{Escape(eventType)}/{Escape(stateKey)}", content, authenticated: true, cancellationToken);
        // Return event ID
        return response.EventId;
    }

    /// <summary>Set the room name via <see cref="SendStateEventAsync"/>.</summary>
    public Task<string> SetRoomNameAsync(string roomId, string name, CancellationToken cancellationToken = default) =>
        SendStateEventAsync(roomId, EventTypes.RoomName, new NameContent { Name = name }, cancellationToken: cancellationToken);

    /// <summary>Set the room topic via <see cref="SendStateEventAsync"/>.</summary>
    public Task<string> SetRoomTopicAsync(string roomId, string topic, CancellationToken cancellationToken = default) =>
        SendStateEventAsync(roomId, EventTypes.RoomTopic, new TopicContent { Topic = topic }, cancellationToken: cancellationToken);

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidmembers">member events</see> of a room.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="membership">Only members with this membership, see <see cref="Memberships"/>; all when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<ClientEvent>> GetMembersAsync(string roomId, string? membership = null, CancellationToken cancellationToken = default)
    {
        // Fetch
        MembersResponse response = await SendAsync<MembersResponse>(HttpMethod.Get, WithQuery($"_matrix/client/v3/rooms/{Escape(roomId)}/members", [("membership", membership)]), body: null, authenticated: true, cancellationToken);
        // Return member events
        return response.Chunk;
    }

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidjoined_members">joined members</see> keyed by user ID.</summary>
    public async Task<IReadOnlyDictionary<string, RoomMember>> GetJoinedMembersAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // Fetch
        JoinedMembersResponse response = await SendAsync<JoinedMembersResponse>(HttpMethod.Get, $"_matrix/client/v3/rooms/{Escape(roomId)}/joined_members", body: null, authenticated: true, cancellationToken);
        // Return members
        return response.Joined;
    }

    /// <summary>Return raw content of one state event, or null when unset.</summary>
    public async Task<JsonElement?> TryGetStateEventAsync(string roomId, string eventType, string stateKey = "", CancellationToken cancellationToken = default)
    {
        // Fetch, mapping not found to null
        try
        {
            return await GetStateEventAsync<JsonElement>(roomId, eventType, stateKey, cancellationToken);
        }
        catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
        {
            return null;
        }
    }
}
