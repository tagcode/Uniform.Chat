namespace Uniform.Matrix.Protocol.Core;

/// <summary>Request of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3createroom">POST /_matrix/client/v3/createRoom</see>.</summary>
public sealed record CreateRoomRequest
{
    /// <summary>Room name.</summary>
    public string? Name { get; init; }
    /// <summary>Room topic.</summary>
    public string? Topic { get; init; }
    /// <summary>Localpart of an alias to create, e.g. <c>general</c> for <c>#general:server</c>.</summary>
    public string? RoomAliasName { get; init; }
    /// <summary>User IDs to invite.</summary>
    public IReadOnlyList<string>? Invite { get; init; }
    /// <summary><c>public</c> to list in the room directory, otherwise <c>private</c>.</summary>
    public string? Visibility { get; init; }
    /// <summary>Preset, see <see cref="RoomPresets"/>.</summary>
    public string? Preset { get; init; }
    /// <summary>Whether this is a direct chat.</summary>
    public bool? IsDirect { get; init; }
    /// <summary>Room version; server default when null.</summary>
    public string? RoomVersion { get; init; }
    /// <summary>Extra state events to set at creation.</summary>
    public IReadOnlyList<InitialStateEvent>? InitialState { get; init; }
}

/// <summary>State event of <see cref="CreateRoomRequest.InitialState"/>.</summary>
public sealed record InitialStateEvent
{
    /// <summary>Event type.</summary>
    public required string Type { get; init; }
    /// <summary>State key.</summary>
    public string StateKey { get; init; } = "";
    /// <summary>Event content; serialized with its runtime type.</summary>
    public required object Content { get; init; }
}

/// <summary>Response holding a room ID, e.g. of join and create room.</summary>
public sealed record RoomIdResponse
{
    /// <summary>Room ID.</summary>
    public required string RoomId { get; init; }
}

/// <summary>Response holding an event ID, e.g. of send and redact.</summary>
public sealed record EventIdResponse
{
    /// <summary>Event ID.</summary>
    public required string EventId { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidaliases">GET /_matrix/client/v3/rooms/{roomId}/aliases</see>.</summary>
public sealed record RoomAliasesResponse
{
    /// <summary>Local aliases of the room.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3directoryroomroomalias">GET /_matrix/client/v3/directory/room/{roomAlias}</see>.</summary>
public sealed record RoomAliasResponse
{
    /// <summary>Room ID the alias points to.</summary>
    public required string RoomId { get; init; }
    /// <summary>Servers that know the room; passes as <c>via</c> when joining.</summary>
    public IReadOnlyList<string> Servers { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3joined_rooms">GET /_matrix/client/v3/joined_rooms</see>.</summary>
public sealed record JoinedRoomsResponse
{
    /// <summary>Joined room IDs.</summary>
    public IReadOnlyList<string> JoinedRooms { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidmembers">GET /_matrix/client/v3/rooms/{roomId}/members</see>.</summary>
public sealed record MembersResponse
{
    /// <summary><see cref="EventTypes.RoomMember"/> events.</summary>
    public IReadOnlyList<ClientEvent> Chunk { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidjoined_members">GET /_matrix/client/v3/rooms/{roomId}/joined_members</see>.</summary>
public sealed record JoinedMembersResponse
{
    /// <summary>Joined members keyed by user ID.</summary>
    public IReadOnlyDictionary<string, RoomMember> Joined { get; init; } = new Dictionary<string, RoomMember>();
}

/// <summary>Joined member of <see cref="JoinedMembersResponse"/>.</summary>
public sealed record RoomMember
{
    /// <summary>Display name in the room.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidmessages">GET /_matrix/client/v3/rooms/{roomId}/messages</see>.</summary>
public sealed record MessagesResponse
{
    /// <summary>Token the page starts at.</summary>
    public required string Start { get; init; }
    /// <summary>Token for the next page; null when there are no more events.</summary>
    public string? End { get; init; }
    /// <summary>Events in the order of the requested <see cref="Direction"/>.</summary>
    public IReadOnlyList<ClientEvent> Chunk { get; init; } = [];
    /// <summary>State needed to show <see cref="Chunk"/>, e.g. lazy-loaded members.</summary>
    public IReadOnlyList<ClientEvent> State { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidcontexteventid">GET /_matrix/client/v3/rooms/{roomId}/context/{eventId}</see>.</summary>
public sealed record ContextResponse
{
    /// <summary>The requested event.</summary>
    public ClientEvent? Event { get; init; }
    /// <summary>Events before it, newest first.</summary>
    public IReadOnlyList<ClientEvent> EventsBefore { get; init; } = [];
    /// <summary>Events after it, oldest first.</summary>
    public IReadOnlyList<ClientEvent> EventsAfter { get; init; } = [];
    /// <summary>State at the event.</summary>
    public IReadOnlyList<ClientEvent> State { get; init; } = [];
    /// <summary>Token for paginating backwards from <see cref="EventsBefore"/>.</summary>
    public string? Start { get; init; }
    /// <summary>Token for paginating forwards from <see cref="EventsAfter"/>.</summary>
    public string? End { get; init; }
}

/// <summary>Pagination direction of <c>MatrixClient.GetMessagesAsync</c>.</summary>
public enum Direction
{
    /// <summary>Towards older events.</summary>
    Backward,
    /// <summary>Towards newer events.</summary>
    Forward,
}
