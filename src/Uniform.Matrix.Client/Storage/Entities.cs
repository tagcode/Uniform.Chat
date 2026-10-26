namespace Uniform.Matrix.Client.Storage;

// Entities are mutable classes rather than records: EF Core tracks them by identity and updates properties in place.

/// <summary>Stored login session and sync position; one row per database.</summary>
public sealed class StoredAccount
{
    /// <summary>Full user ID; primary key.</summary>
    public required string UserId { get; set; }
    /// <summary>Homeserver base URL.</summary>
    public required string Homeserver { get; set; }
    /// <summary>Device ID of the session.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Access token; null after logout or when the server rejected it.</summary>
    public string? AccessToken { get; set; }
    /// <summary>Last applied <c>next_batch</c>; syncs resumes from here.</summary>
    public string? NextBatch { get; set; }
    /// <summary>Last change time.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Room event received from the server.</summary>
public sealed class StoredEvent
{
    /// <summary>Event ID; primary key.</summary>
    public required string EventId { get; set; }
    /// <summary>Room of the event.</summary>
    public required string RoomId { get; set; }
    /// <summary>Event type.</summary>
    public required string Type { get; set; }
    /// <summary>Sender user ID.</summary>
    public required string Sender { get; set; }
    /// <summary>State key of state events.</summary>
    public string? StateKey { get; set; }
    /// <summary>Server timestamp in milliseconds; orders the room history.</summary>
    public long OriginServerTs { get; set; }
    /// <summary>Whether the content was removed by a redaction.</summary>
    public bool Redacted { get; set; }
    /// <summary>Full event as JSON in <see cref="Uniform.Matrix.Protocol.Core.MatrixJson"/> format.</summary>
    public required string Json { get; set; }
}

/// <summary>Delivery state of an <see cref="OutboxMessage"/>.</summary>
public enum OutboxState
{
    /// <summary>Waiting to be sent or retried.</summary>
    Pending,
    /// <summary>Accepted by the server.</summary>
    Sent,
    /// <summary>Rejected by the server; will not be retried.</summary>
    Failed,
}

/// <summary>Event written locally and not yet confirmed by the server.</summary>
public sealed class OutboxMessage
{
    /// <summary>Local ID; primary key, also send order.</summary>
    public long Id { get; set; }
    /// <summary>Target room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Event type, e.g. <c>m.room.message</c>.</summary>
    public required string EventType { get; set; }
    /// <summary>Event content as JSON.</summary>
    public required string ContentJson { get; set; }
    /// <summary>Transaction ID fixed at enqueue, so resends after a crash are deduplicated by the server.</summary>
    public required string TransactionId { get; set; }
    /// <summary>Delivery state.</summary>
    public OutboxState State { get; set; }
    /// <summary>Event ID assigned by the server once sent.</summary>
    public string? EventId { get; set; }
    /// <summary>Number of send attempts.</summary>
    public int Attempts { get; set; }
    /// <summary>Error of the last failed attempt.</summary>
    public string? LastError { get; set; }
    /// <summary>Enqueue time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Time the server accepted the event.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Downloaded media file kept in the media directory.</summary>
public sealed class StoredMedia
{
    /// <summary>Content URI <c>mxc://server/mediaId</c>; primary key.</summary>
    public required string MxcUri { get; set; }
    /// <summary>File path relative to the media directory.</summary>
    public required string RelativePath { get; set; }
    /// <summary>MIME type.</summary>
    public string? ContentType { get; set; }
    /// <summary>Original file name.</summary>
    public string? FileName { get; set; }
    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }
    /// <summary>Download time.</summary>
    public DateTimeOffset DownloadedAt { get; set; }
}

/// <summary>Stored encryption account of this device; one row.</summary>
public sealed class CryptoAccountRow
{
    /// <summary>Always 1.</summary>
    public int Id { get; set; } = 1;
    /// <summary>Device the account belongs to; a new login gets a new account.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Encrypted Olm account pickle.</summary>
    public required string Pickle { get; set; }
    /// <summary>Whether device keys were uploaded.</summary>
    public bool DeviceKeysUploaded { get; set; }
}

/// <summary>Stored Olm session with another device.</summary>
public sealed class CryptoOlmSessionRow
{
    /// <summary>Curve25519 key of the other device.</summary>
    public required string SenderKey { get; set; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; set; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; set; }
    /// <summary>Last use in UTC ticks.</summary>
    public long LastUsedTicks { get; set; }
}

/// <summary>Stored Megolm session for decrypting room messages.</summary>
public sealed class CryptoInboundGroupSessionRow
{
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; set; }
    /// <summary>Curve25519 key of the device the key came from.</summary>
    public required string SenderKey { get; set; }
    /// <summary>Ed25519 key claimed by that device.</summary>
    public required string SigningKey { get; set; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; set; }
    /// <summary>Whether the session is in the current key backup.</summary>
    public bool BackedUp { get; set; }
    /// <summary>Whether the key came from a backup or another device.</summary>
    public bool Imported { get; set; }
}

/// <summary>Stored Megolm session for sending to a room.</summary>
public sealed class CryptoOutboundGroupSessionRow
{
    /// <summary>Room; primary key.</summary>
    public required string RoomId { get; set; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; set; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; set; }
    /// <summary>Creation in UTC ticks.</summary>
    public long CreatedAtTicks { get; set; }
    /// <summary>Messages sent.</summary>
    public int MessageCount { get; set; }
    /// <summary>Devices that have the key, as a JSON array of <c>user|device</c>.</summary>
    public required string SharedWithJson { get; set; }
}

/// <summary>Stored device list of a user.</summary>
public sealed class CryptoDeviceListRow
{
    /// <summary>User; primary key.</summary>
    public required string UserId { get; set; }
    /// <summary>Verified devices as JSON.</summary>
    public required string DevicesJson { get; set; }
    /// <summary>Whether the list must be fetched again.</summary>
    public bool Outdated { get; set; }
    /// <summary>Cross-signing keys as JSON, when the user has them.</summary>
    public string? IdentityJson { get; set; }
}

/// <summary>Named encryption value, e.g. a private key encrypted with the pickle key, or the backup version.</summary>
public sealed class CryptoValueRow
{
    /// <summary>Name; primary key.</summary>
    public required string Name { get; set; }
    /// <summary>Value.</summary>
    public required string Value { get; set; }
}
