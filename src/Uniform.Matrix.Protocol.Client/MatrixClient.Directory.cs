namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

// Room aliases, public room directory and user directory.
public sealed partial class MatrixClient
{
    /// <summary>Return a page of the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3publicrooms">public room directory</see>, like IRC <c>/list</c>.</summary>
    /// <param name="server">Server whose directory to list; this homeserver when null.</param>
    /// <param name="limit">Maximum number of rooms; server default when null.</param>
    /// <param name="since"><see cref="PublicRoomsResponse.NextBatch"/> of the previous page.</param>
    /// <param name="searchTerm">Text to match in name, topic and alias; uses the <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3publicrooms">filtered</see> form when set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PublicRoomsResponse> GetPublicRoomsAsync(string? server = null, int? limit = null, string? since = null, string? searchTerm = null, CancellationToken cancellationToken = default)
    {
        // Unfiltered listing
        if (searchTerm is null) return SendAsync<PublicRoomsResponse>(HttpMethod.Get, WithQuery("_matrix/client/v3/publicRooms", [("server", server), ("limit", limit?.ToString()), ("since", since)]), body: null, authenticated: true, cancellationToken);

        // Filtered search
        object body = new { limit, since, filter = new { generic_search_term = searchTerm } };
        return SendAsync<PublicRoomsResponse>(HttpMethod.Post, WithQuery("_matrix/client/v3/publicRooms", [("server", server)]), body, authenticated: true, cancellationToken);
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3user_directorysearch">Searches users</see> by user ID or display name, e.g. for <c>/whois</c> or completion.</summary>
    /// <param name="searchTerm">Text to match.</param>
    /// <param name="limit">Maximum number of results; server default when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<UserDirectoryResponse> SearchUsersAsync(string searchTerm, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync<UserDirectoryResponse>(HttpMethod.Post, "_matrix/client/v3/user_directory/search", new { search_term = searchTerm, limit }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3directoryroomroomalias">Creates</see> an alias like <c>#room:server</c> for a room.</summary>
    public Task CreateRoomAliasAsync(string roomAlias, string roomId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/directory/room/{Escape(roomAlias)}", new { room_id = roomId }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#delete_matrixclientv3directoryroomroomalias">Deletes</see> a room alias.</summary>
    public Task DeleteRoomAliasAsync(string roomAlias, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_matrix/client/v3/directory/room/{Escape(roomAlias)}", body: null, authenticated: true, cancellationToken);

    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidaliases">local aliases</see> of a room.</summary>
    public async Task<IReadOnlyList<string>> GetRoomAliasesAsync(string roomId, CancellationToken cancellationToken = default) =>
        (await SendAsync<RoomAliasesResponse>(HttpMethod.Get, $"_matrix/client/v3/rooms/{Escape(roomId)}/aliases", body: null, authenticated: true, cancellationToken)).Aliases;

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3directorylistroomroomid">Lists or unlists</see> a room in the public room directory.</summary>
    public Task SetRoomDirectoryVisibilityAsync(string roomId, bool listed, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/directory/list/room/{Escape(roomId)}", new { visibility = listed ? "public" : "private" }, authenticated: true, cancellationToken);
}
