namespace Uniform.Matrix.Protocol.Core;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3publicrooms">GET /_matrix/client/v3/publicRooms</see>.</summary>
public sealed record PublicRoomsResponse
{
    /// <summary>Rooms of this page.</summary>
    public IReadOnlyList<PublicRoom> Chunk { get; init; } = [];
    /// <summary>Token for the next page; null on the last page.</summary>
    public string? NextBatch { get; init; }
    /// <summary>Token for the previous page.</summary>
    public string? PrevBatch { get; init; }
    /// <summary>Estimated number of rooms in the directory.</summary>
    public int? TotalRoomCountEstimate { get; init; }
}

/// <summary>Room listed in the public room directory.</summary>
public sealed record PublicRoom
{
    /// <summary>Room ID.</summary>
    public required string RoomId { get; init; }
    /// <summary>Room name.</summary>
    public string? Name { get; init; }
    /// <summary>Room topic.</summary>
    public string? Topic { get; init; }
    /// <summary>Main alias.</summary>
    public string? CanonicalAlias { get; init; }
    /// <summary>Number of joined members.</summary>
    public int NumJoinedMembers { get; init; }
    /// <summary>Whether guests can read the room without joining.</summary>
    public bool WorldReadable { get; init; }
    /// <summary>Whether guests can join.</summary>
    public bool GuestCanJoin { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>Join rule, e.g. <c>public</c> or <c>knock</c>.</summary>
    public string? JoinRule { get; init; }
    /// <summary>Room type, e.g. <c>m.space</c>; null for chat rooms.</summary>
    public string? RoomType { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3user_directorysearch">POST /_matrix/client/v3/user_directory/search</see>.</summary>
public sealed record UserDirectoryResponse
{
    /// <summary>Matching users.</summary>
    public IReadOnlyList<UserDirectoryEntry> Results { get; init; } = [];
    /// <summary>Whether more results exist beyond the limit.</summary>
    public bool Limited { get; init; }
}

/// <summary>User found by <c>MatrixClient.SearchUsersAsync</c>.</summary>
public sealed record UserDirectoryEntry
{
    /// <summary>Full user ID.</summary>
    public required string UserId { get; init; }
    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixmediav3upload">POST /_matrix/media/v3/upload</see>.</summary>
public sealed record UploadResponse
{
    /// <summary>Content URI of the uploaded media.</summary>
    public required string ContentUri { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixmediav1create">creating media</see> before uploading its content.</summary>
public sealed record CreateMediaResponse
{
    /// <summary>The reserved <c>mxc://</c> URI.</summary>
    public required string ContentUri { get; init; }
    /// <summary>When the URI can no longer be uploaded to, in milliseconds since 1970.</summary>
    public long? UnusedExpiresAt { get; init; }
}
