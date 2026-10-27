namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

// Account data, tags, ignored users and direct rooms.
public sealed partial class MatrixClient
{
    /// <summary>Return global <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3useruseridaccount_datatype">account data</see> of <paramref name="type"/>; throw <see cref="MatrixException"/> with <see cref="ErrorCodes.NotFound"/> when unset.</summary>
    public Task<T> GetAccountDataAsync<T>(string type, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/account_data/{Escape(type)}", body: null, authenticated: true, cancellationToken);

    /// <summary>Return global account data of <paramref name="type"/>, or null when unset.</summary>
    public Task<T?> TryGetAccountDataAsync<T>(string type, CancellationToken cancellationToken = default) where T : class =>
        NullIfNotFoundAsync(GetAccountDataAsync<T>(type, cancellationToken));

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3useruseridaccount_datatype">Sets</see> global account data of <paramref name="type"/>.</summary>
    public Task SetAccountDataAsync(string type, object content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/account_data/{Escape(type)}", content, authenticated: true, cancellationToken);

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3useruseridroomsroomidaccount_datatype">room account data</see> of <paramref name="type"/>; throw <see cref="MatrixException"/> with <see cref="ErrorCodes.NotFound"/> when unset.</summary>
    public Task<T> GetRoomAccountDataAsync<T>(string roomId, string type, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/rooms/{Escape(roomId)}/account_data/{Escape(type)}", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3useruseridroomsroomidaccount_datatype">Sets</see> room account data of <paramref name="type"/>.</summary>
    public Task SetRoomAccountDataAsync(string roomId, string type, object content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/rooms/{Escape(roomId)}/account_data/{Escape(type)}", content, authenticated: true, cancellationToken);

    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3useruseridroomsroomidtags">tags</see> of a room.</summary>
    public Task<TagContent> GetTagsAsync(string roomId, CancellationToken cancellationToken = default) =>
        SendAsync<TagContent>(HttpMethod.Get, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/rooms/{Escape(roomId)}/tags", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3useruseridroomsroomidtagstag">Adds</see> <paramref name="tag"/> to a room, see <see cref="RoomTags"/>.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="tag">Tag name; custom tags use reverse-DNS names like <c>u.work</c>.</param>
    /// <param name="order">Position among rooms with the same tag, from 0 to 1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetTagAsync(string roomId, string tag, double? order = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/rooms/{Escape(roomId)}/tags/{Escape(tag)}", new TagInfo { Order = order }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#delete_matrixclientv3useruseridroomsroomidtagstag">Removes</see> <paramref name="tag"/> from a room.</summary>
    public Task DeleteTagAsync(string roomId, string tag, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/rooms/{Escape(roomId)}/tags/{Escape(tag)}", body: null, authenticated: true, cancellationToken);

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#ignoring-users">ignored</see> user IDs.</summary>
    public async Task<IReadOnlyCollection<string>> GetIgnoredUsersAsync(CancellationToken cancellationToken = default)
    {
        // Fetch list
        IgnoredUserListContent? content = await TryGetAccountDataAsync<IgnoredUserListContent>(EventTypes.IgnoredUserList, cancellationToken);
        // Return user IDs
        return content?.IgnoredUsers.Keys.ToList() ?? [];
    }

    /// <summary>Ignore <paramref name="userId"/>; the server then hides their events and invite.</summary>
    public Task IgnoreUserAsync(string userId, CancellationToken cancellationToken = default) => UpdateIgnoredUsersAsync(users => users.Add(userId), cancellationToken);

    /// <summary>Stop ignoring <paramref name="userId"/>.</summary>
    public Task UnignoreUserAsync(string userId, CancellationToken cancellationToken = default) => UpdateIgnoredUsersAsync(users => users.Remove(userId), cancellationToken);

    /// <summary>Read the ignored user list, applies <paramref name="update"/> and write it back when it changed.</summary>
    private async Task UpdateIgnoredUsersAsync(Func<HashSet<string>, bool> update, CancellationToken cancellationToken)
    {
        // Read and modify
        HashSet<string> users = [.. await GetIgnoredUsersAsync(cancellationToken)];
        if (!update(users)) return;
        // Write back; values must be empty objects
        IgnoredUserListContent content = new() { IgnoredUsers = users.ToDictionary(u => u, _ => new object()) };
        await SetAccountDataAsync(EventTypes.IgnoredUserList, content, cancellationToken);
    }

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#direct-messaging">direct-message</see> rooms keyed by the other user's ID.</summary>
    public async Task<DirectContent> GetDirectRoomsAsync(CancellationToken cancellationToken = default)
    {
        // Fetch
        DirectContent? content = await TryGetAccountDataAsync<DirectContent>(EventTypes.Direct, cancellationToken);
        // Return rooms, empty when unset
        return content ?? [];
    }

    /// <summary>Create a direct-message room with <paramref name="userId"/>, invites them and record it in <see cref="EventTypes.Direct"/>; return the room ID.</summary>
    /// <param name="userId">User to chat with.</param>
    /// <param name="encrypted">Whether to turn on end-to-end encryption from the start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> CreateDirectRoomAsync(string userId, bool encrypted = false, CancellationToken cancellationToken = default)
    {
        // Create room
        IReadOnlyList<InitialStateEvent>? state = encrypted ? [new InitialStateEvent { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } }] : null;
        CreateRoomRequest request = new() { IsDirect = true, Invite = [userId], Preset = RoomPresets.TrustedPrivateChat, InitialState = state };
        string roomId = await CreateRoomAsync(request, cancellationToken);
        await MarkDirectRoomAsync(userId, roomId, cancellationToken);
        // Return room ID
        return roomId;
    }

    /// <summary>Record <paramref name="roomId"/> as a direct chat with <paramref name="userId"/> in <see cref="EventTypes.Direct"/>, e.g. after joining an invite marked <c>is_direct</c>; nothing changes when it is recorded already.</summary>
    /// <param name="userId">The other person.</param>
    /// <param name="roomId">Room of the chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task MarkDirectRoomAsync(string userId, string roomId, CancellationToken cancellationToken = default)
    {
        // Read, add once, write back
        DirectContent direct = await GetDirectRoomsAsync(cancellationToken);
        if (!direct.TryGetValue(userId, out List<string>? roomIds)) direct[userId] = roomIds = [];
        if (roomIds.Contains(roomId)) return;
        roomIds.Add(roomId);
        // Done
        await SetAccountDataAsync(EventTypes.Direct, direct, cancellationToken);
    }

    /// <summary>Await <paramref name="task"/>, mapping <see cref="ErrorCodes.NotFound"/> to null.</summary>
    private static async Task<T?> NullIfNotFoundAsync<T>(Task<T> task) where T : class
    {
        // Await, mapping not found to null
        try
        {
            return await task;
        }
        catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
        {
            return null;
        }
    }
}
