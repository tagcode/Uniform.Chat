namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3sync">GET /_matrix/client/v3/sync</see>.</summary>
public sealed record SyncResponse
{
    /// <summary>Token to pass as <c>since</c> in the next sync.</summary>
    public required string NextBatch { get; init; }
    /// <summary>Updates to rooms.</summary>
    public SyncRooms Rooms { get; init; } = new();
    /// <summary>Presence updates of other users.</summary>
    public EventList<PresenceEvent> Presence { get; init; } = new();
    /// <summary>Global account data.</summary>
    public EventList<MatrixEvent> AccountData { get; init; } = new();
    /// <summary>Messages sent directly to this device, e.g. encryption keys.</summary>
    public EventList<ToDeviceEvent> ToDevice { get; init; } = new();
    /// <summary>Users whose device lists changed; used by end-to-end encryption.</summary>
    public DeviceLists DeviceLists { get; init; } = new();
    /// <summary>Unclaimed one-time keys of this device on the server by algorithm, to keep them topped up.</summary>
    public IReadOnlyDictionary<string, int>? DeviceOneTimeKeysCount { get; init; }
    /// <summary>Algorithms of this device's fallback keys not yet used; null when the server does not report them.</summary>
    public IReadOnlyList<string>? DeviceUnusedFallbackKeyTypes { get; init; }
}

/// <summary>Device list changes of a <see cref="SyncResponse"/>.</summary>
public sealed record DeviceLists
{
    /// <summary>Users whose devices changed.</summary>
    public IReadOnlyList<string> Changed { get; init; } = [];
    /// <summary>Users who no longer share an encrypted room with this user.</summary>
    public IReadOnlyList<string> Left { get; init; } = [];
}

/// <summary>Room updates of a <see cref="SyncResponse"/> by membership, keyed by room ID.</summary>
public sealed record SyncRooms
{
    /// <summary>Rooms the user has joined.</summary>
    public IReadOnlyDictionary<string, JoinedRoom> Join { get; init; } = new Dictionary<string, JoinedRoom>();
    /// <summary>Rooms the user is invited to.</summary>
    public IReadOnlyDictionary<string, InvitedRoom> Invite { get; init; } = new Dictionary<string, InvitedRoom>();
    /// <summary>Rooms the user has left or was removed from.</summary>
    public IReadOnlyDictionary<string, LeftRoom> Leave { get; init; } = new Dictionary<string, LeftRoom>();
    /// <summary>Rooms the user has asked to join.</summary>
    public IReadOnlyDictionary<string, KnockedRoom> Knock { get; init; } = new Dictionary<string, KnockedRoom>();
}

/// <summary>Update of a room the user knocked on.</summary>
public sealed record KnockedRoom
{
    /// <summary>Partial room state for showing the knock.</summary>
    public EventList<StrippedStateEvent> KnockState { get; init; } = new();
}

/// <summary>Update of a joined room.</summary>
public sealed record JoinedRoom
{
    /// <summary>Data for computing the room name.</summary>
    public RoomSummary? Summary { get; init; }
    /// <summary>State before <see cref="Timeline"/>; only changes since last sync.</summary>
    public EventList<ClientEvent> State { get; init; } = new();
    /// <summary>New room events, including state events.</summary>
    public Timeline Timeline { get; init; } = new();
    /// <summary>Typing notifications and receipts.</summary>
    public EventList<MatrixEvent> Ephemeral { get; init; } = new();
    /// <summary>Room account data, e.g. <see cref="EventTypes.FullyRead"/>.</summary>
    public EventList<MatrixEvent> AccountData { get; init; } = new();
    /// <summary>Unread counts.</summary>
    public UnreadNotificationCounts? UnreadNotifications { get; init; }
}

/// <summary>Update of an invited room.</summary>
public sealed record InvitedRoom
{
    /// <summary>Partial room state for showing the invite.</summary>
    public EventList<StrippedStateEvent> InviteState { get; init; } = new();
}

/// <summary>Update of a left room.</summary>
public sealed record LeftRoom
{
    /// <summary>State before <see cref="Timeline"/>.</summary>
    public EventList<ClientEvent> State { get; init; } = new();
    /// <summary>Events up to leaving.</summary>
    public Timeline Timeline { get; init; } = new();
    /// <summary>Room account data.</summary>
    public EventList<MatrixEvent> AccountData { get; init; } = new();
}

/// <summary>Timeline section of a room update.</summary>
public sealed record Timeline
{
    /// <summary>Events in chronological order.</summary>
    public IReadOnlyList<ClientEvent> Events { get; init; } = [];
    /// <summary>True when events were skipped; fetches them with <see cref="PrevBatch"/>.</summary>
    public bool Limited { get; init; }
    /// <summary>Token for <c>MatrixClient.GetMessagesAsync</c> to fetch older events.</summary>
    public string? PrevBatch { get; init; }
}

/// <summary>List of events.</summary>
public sealed record EventList<T> where T : MatrixEvent
{
    /// <summary>Events.</summary>
    public IReadOnlyList<T> Events { get; init; } = [];
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-room">Room summary</see> for computing a room name.</summary>
public sealed record RoomSummary
{
    /// <summary>Users to name the room after when it has no name or alias.</summary>
    [JsonPropertyName("m.heroes")]
    public IReadOnlyList<string>? Heroes { get; init; }
    /// <summary>Number of joined members.</summary>
    [JsonPropertyName("m.joined_member_count")]
    public int? JoinedMemberCount { get; init; }
    /// <summary>Number of invited members.</summary>
    [JsonPropertyName("m.invited_member_count")]
    public int? InvitedMemberCount { get; init; }
}

/// <summary>Unread notification counts of a room.</summary>
public sealed record UnreadNotificationCounts
{
    /// <summary>Unread highlights, e.g. mentions.</summary>
    public int? HighlightCount { get; init; }
    /// <summary>Unread notifying messages.</summary>
    public int? NotificationCount { get; init; }
}
