namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3adminwhoisuserid">whois</see>: a user's devices and their connections.</summary>
public sealed record WhoisResponse
{
    /// <summary>The user.</summary>
    public string UserId { get; init; } = "";
    /// <summary>Devices by device ID.</summary>
    public IReadOnlyDictionary<string, WhoisDevice> Devices { get; init; } = new Dictionary<string, WhoisDevice>();
}

/// <summary>A device in a <see cref="WhoisResponse"/>.</summary>
public sealed record WhoisDevice
{
    /// <summary>Sessions of the device; servers usually give one.</summary>
    public IReadOnlyList<WhoisSession> Sessions { get; init; } = [];
    /// <summary>Device name, which this project's server adds.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>A session of a <see cref="WhoisDevice"/>.</summary>
public sealed record WhoisSession
{
    /// <summary>Connections of the session.</summary>
    public IReadOnlyList<WhoisConnection> Connections { get; init; } = [];
}

/// <summary>A connection of a <see cref="WhoisSession"/>.</summary>
public sealed record WhoisConnection
{
    /// <summary>Address the connection came from.</summary>
    public string? Ip { get; init; }
    /// <summary>Last use, in milliseconds since the Unix epoch.</summary>
    public long? LastSeen { get; init; }
    /// <summary>User agent of the client.</summary>
    public string? UserAgent { get; init; }
}

/// <summary>A local account in <see href="https://element-hq.github.io/synapse/latest/admin_api/user_admin_api.html">Synapse's user admin API</see>.</summary>
public sealed record AdminAccount
{
    /// <summary>User ID.</summary>
    public string Name { get; init; } = "";
    /// <summary>Display name.</summary>
    [JsonPropertyName("displayname")]
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>Whether the user administers the server.</summary>
    public bool Admin { get; init; }
    /// <summary>Whether the account is deactivated.</summary>
    public bool Deactivated { get; init; }
    /// <summary>Whether the account is locked.</summary>
    public bool Locked { get; init; }
    /// <summary>Whether the account is suspended.</summary>
    public bool Suspended { get; init; }
    /// <summary>Creation, in milliseconds since the Unix epoch.</summary>
    public long? CreationTs { get; init; }
    /// <summary>Logged-in devices, which this project's server adds.</summary>
    public int? Devices { get; init; }
    /// <summary>Joined rooms, which this project's server adds.</summary>
    public int? JoinedRooms { get; init; }
}

/// <summary>A page of <see cref="AdminAccount"/>s.</summary>
public sealed record AdminAccountsResponse
{
    /// <summary>The accounts.</summary>
    public IReadOnlyList<AdminAccount> Users { get; init; } = [];
    /// <summary>All matching accounts.</summary>
    public int Total { get; init; }
    /// <summary>The <c>from</c> of the next page; null on the last page.</summary>
    public string? NextToken { get; init; }
}

/// <summary>A registration token in <see href="https://element-hq.github.io/synapse/latest/usage/administration/admin_api/registration_tokens.html">Synapse's admin API</see>.</summary>
public sealed record AdminRegistrationToken
{
    /// <summary>The token.</summary>
    public string Token { get; init; } = "";
    /// <summary>How many registrations may use it; null for any number.</summary>
    public int? UsesAllowed { get; init; }
    /// <summary>Registrations in progress with it.</summary>
    public int Pending { get; init; }
    /// <summary>Registrations that used it.</summary>
    public int Completed { get; init; }
    /// <summary>Expiry, in milliseconds since the Unix epoch; null for never.</summary>
    public long? ExpiryTime { get; init; }
}

/// <summary>Response listing <see cref="AdminRegistrationToken"/>s.</summary>
public sealed record AdminRegistrationTokensResponse
{
    /// <summary>The tokens.</summary>
    public IReadOnlyList<AdminRegistrationToken> RegistrationTokens { get; init; } = [];
}

/// <summary>A report in Synapse's event report admin API; this project's server lists reports of rooms and users there too.</summary>
public sealed record AdminReport
{
    /// <summary>Report ID.</summary>
    public long Id { get; init; }
    /// <summary>When it was received, in milliseconds since the Unix epoch.</summary>
    public long ReceivedTs { get; init; }
    /// <summary>Reported room, or the room of the reported event.</summary>
    public string? RoomId { get; init; }
    /// <summary>Reported event.</summary>
    public string? EventId { get; init; }
    /// <summary>Who reported.</summary>
    public string UserId { get; init; } = "";
    /// <summary>Reported user, which this project's server adds.</summary>
    public string? TargetUserId { get; init; }
    /// <summary>The reporter's reason.</summary>
    public string? Reason { get; init; }
    /// <summary>The reported event's sender and text, which this project's server adds.</summary>
    public string? EventSummary { get; init; }
    /// <summary>Whether an admin resolved it, which this project's server adds.</summary>
    public bool Resolved { get; init; }
}

/// <summary>A page of <see cref="AdminReport"/>s.</summary>
public sealed record AdminReportsResponse
{
    /// <summary>The reports.</summary>
    public IReadOnlyList<AdminReport> EventReports { get; init; } = [];
    /// <summary>All reports.</summary>
    public int Total { get; init; }
    /// <summary>The <c>from</c> of the next page; null on the last page.</summary>
    public string? NextToken { get; init; }
}

/// <summary>A room in Synapse's room admin API.</summary>
public sealed record AdminRoomInfo
{
    /// <summary>Room ID.</summary>
    public string RoomId { get; init; } = "";
    /// <summary>Name.</summary>
    public string? Name { get; init; }
    /// <summary>Canonical alias.</summary>
    public string? CanonicalAlias { get; init; }
    /// <summary>Joined members.</summary>
    public int JoinedMembers { get; init; }
    /// <summary>Room version.</summary>
    public string? Version { get; init; }
    /// <summary>Creator.</summary>
    public string? Creator { get; init; }
    /// <summary>Encryption algorithm; null when not encrypted.</summary>
    public string? Encryption { get; init; }
    /// <summary>Whether it is in the public room directory.</summary>
    public bool Public { get; init; }
}

/// <summary>A page of <see cref="AdminRoomInfo"/>s.</summary>
public sealed record AdminRoomsResponse
{
    /// <summary>The rooms.</summary>
    public IReadOnlyList<AdminRoomInfo> Rooms { get; init; } = [];
    /// <summary>Offset of this page.</summary>
    public int Offset { get; init; }
    /// <summary>All matching rooms.</summary>
    public int TotalRooms { get; init; }
    /// <summary>The <c>from</c> of the next page; null on the last page.</summary>
    public int? NextBatch { get; init; }
}

/// <summary>Response of shutting down a room.</summary>
public sealed record AdminRoomShutdown
{
    /// <summary>Local users made to leave.</summary>
    public IReadOnlyList<string> KickedUsers { get; init; } = [];
    /// <summary>Local users who could not be made to leave.</summary>
    public IReadOnlyList<string> FailedToKickUsers { get; init; } = [];
    /// <summary>Local aliases deleted.</summary>
    public IReadOnlyList<string> LocalAliases { get; init; } = [];
}
