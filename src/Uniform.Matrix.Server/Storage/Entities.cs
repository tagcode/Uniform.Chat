namespace Uniform.Matrix.Server.Storage;

// Entities are mutable classes rather than records: EF Core tracks them by identity and updates properties in place.

/// <summary>A local user account.</summary>
public sealed class UserRow
{
    /// <summary>Full user ID; primary key.</summary>
    public required string UserId { get; set; }
    /// <summary>Password hash from <c>PasswordHasher</c>; null for accounts without a password.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>Creation in milliseconds since the Unix epoch.</summary>
    public long CreatedTs { get; set; }
    /// <summary>Whether the account is deactivated and cannot log in.</summary>
    public bool Deactivated { get; set; }
    /// <summary>Whether the user administers the server.</summary>
    public bool Admin { get; set; }
}

/// <summary>A device of a local user.</summary>
public sealed class DeviceRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Device ID, unique per user.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Name shown in device lists.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Last request in milliseconds since the Unix epoch.</summary>
    public long? LastSeenTs { get; set; }
    /// <summary>Address of the last request.</summary>
    public string? LastSeenIp { get; set; }
}

/// <summary>An access token, stored as its SHA-256 hash so a leaked database does not leak sessions.</summary>
public sealed class AccessTokenRow
{
    /// <summary>Hex SHA-256 of the token; primary key.</summary>
    public required string TokenHash { get; set; }
    /// <summary>User the token acts as.</summary>
    public required string UserId { get; set; }
    /// <summary>Device the token belongs to.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Creation in milliseconds since the Unix epoch.</summary>
    public long CreatedTs { get; set; }
}

/// <summary>A user's profile.</summary>
public sealed class ProfileRow
{
    /// <summary>Full user ID; primary key.</summary>
    public required string UserId { get; set; }
    /// <summary>Display name.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; set; }
}

/// <summary>A room known to the server.</summary>
public sealed class RoomRow
{
    /// <summary>Room ID; primary key.</summary>
    public required string RoomId { get; set; }
    /// <summary>Room version, e.g. <c>10</c>.</summary>
    public required string Version { get; set; }
    /// <summary>User who created the room.</summary>
    public required string Creator { get; set; }
    /// <summary>Creation in milliseconds since the Unix epoch.</summary>
    public long CreatedTs { get; set; }
    /// <summary>State group of the current state.</summary>
    public long CurrentStateGroup { get; set; }
    /// <summary>Whether the room is listed in the public room directory.</summary>
    public bool Published { get; set; }
}

/// <summary>An event of the room graph, stored as its PDU JSON without the event ID.</summary>
public sealed class EventRow
{
    /// <summary>Event ID; primary key.</summary>
    public required string EventId { get; set; }
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Position in the server's stream of events, which sync tokens count; unique and increasing.</summary>
    public long Stream { get; set; }
    /// <summary>Depth in the room graph.</summary>
    public long Depth { get; set; }
    /// <summary>Event type.</summary>
    public required string Type { get; set; }
    /// <summary>State key of state events; null for others.</summary>
    public string? StateKey { get; set; }
    /// <summary>Sender.</summary>
    public required string Sender { get; set; }
    /// <summary>Origin server time in milliseconds since the Unix epoch.</summary>
    public long OriginServerTs { get; set; }
    /// <summary>The PDU; its redacted form once redacted.</summary>
    public required string Json { get; set; }
    /// <summary>State group of the room's state after this event.</summary>
    public long StateGroup { get; set; }
    /// <summary>Redaction that removed this event's content, when any.</summary>
    public string? RedactedBy { get; set; }
}

/// <summary>Edge of the room graph: an event and one of its previous events.</summary>
public sealed class EventEdgeRow
{
    /// <summary>Event.</summary>
    public required string EventId { get; set; }
    /// <summary>One of its <c>prev_events</c>.</summary>
    public required string PrevEventId { get; set; }
}

/// <summary>A forward extremity: an event of the room that no other event follows yet; new events point at these.</summary>
public sealed class ForwardExtremityRow
{
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Event.</summary>
    public required string EventId { get; set; }
}

/// <summary>A room state snapshot, stored as changes against a parent group.</summary>
public sealed class StateGroupRow
{
    /// <summary>Group ID; primary key.</summary>
    public long Id { get; set; }
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Group these changes apply to; null for a full snapshot.</summary>
    public long? ParentId { get; set; }
    /// <summary>Length of the parent chain, to keep lookups short.</summary>
    public int ChainLength { get; set; }
}

/// <summary>One state entry of a state group.</summary>
public sealed class StateGroupEntryRow
{
    /// <summary>Group.</summary>
    public long GroupId { get; set; }
    /// <summary>State event type.</summary>
    public required string Type { get; set; }
    /// <summary>State key.</summary>
    public required string StateKey { get; set; }
    /// <summary>Event holding this state.</summary>
    public required string EventId { get; set; }
}

/// <summary>The current state of a room, one row per type and state key, for fast reads.</summary>
public sealed class CurrentStateRow
{
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>State event type.</summary>
    public required string Type { get; set; }
    /// <summary>State key.</summary>
    public required string StateKey { get; set; }
    /// <summary>Event holding this state.</summary>
    public required string EventId { get; set; }
    /// <summary>For membership events, the membership, so a user's rooms are one query.</summary>
    public string? Membership { get; set; }
    /// <summary>Stream position of the event.</summary>
    public long Stream { get; set; }
}

/// <summary>A client transaction ID mapped to the event it created, so retried sends are not duplicated.</summary>
public sealed class TransactionRow
{
    /// <summary>Sender.</summary>
    public required string UserId { get; set; }
    /// <summary>Sending device.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Client transaction ID.</summary>
    public required string TransactionId { get; set; }
    /// <summary>Created event.</summary>
    public required string EventId { get; set; }
}

/// <summary>A local room alias.</summary>
public sealed class RoomAliasRow
{
    /// <summary>Alias, e.g. <c>#general:example.org</c>; primary key.</summary>
    public required string Alias { get; set; }
    /// <summary>Room it points to.</summary>
    public required string RoomId { get; set; }
    /// <summary>User who created it.</summary>
    public required string Creator { get; set; }
}

/// <summary>A room a user has forgotten, so it no longer shows in their sync.</summary>
public sealed class ForgottenRoomRow
{
    /// <summary>User.</summary>
    public required string UserId { get; set; }
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
}

/// <summary>A stored sync filter of a user.</summary>
public sealed class FilterRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Filter ID, unique per user.</summary>
    public required string FilterId { get; set; }
    /// <summary>Filter JSON.</summary>
    public required string Json { get; set; }
}


/// <summary>Account data of a user: global when <see cref="RoomId"/> is empty, else for one room.</summary>
public sealed class AccountDataRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Room, or empty for global account data.</summary>
    public required string RoomId { get; set; }
    /// <summary>Event type, e.g. <c>m.direct</c> or <c>m.tag</c>.</summary>
    public required string Type { get; set; }
    /// <summary>Content JSON.</summary>
    public required string Json { get; set; }
    /// <summary>Stream position of the last change, for incremental sync.</summary>
    public long Stream { get; set; }
}

/// <summary>A user's latest receipt of one type and thread in a room.</summary>
public sealed class ReceiptRow
{
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>User.</summary>
    public required string UserId { get; set; }
    /// <summary>Receipt type: <c>m.read</c> or <c>m.read.private</c>.</summary>
    public required string Type { get; set; }
    /// <summary>Thread ID, <c>main</c> for the main timeline, or empty for an unthreaded receipt.</summary>
    public required string ThreadId { get; set; }
    /// <summary>Event read up to.</summary>
    public required string EventId { get; set; }
    /// <summary>Time of the receipt in milliseconds since the Unix epoch.</summary>
    public long Ts { get; set; }
    /// <summary>Stream position of the last change, for incremental sync.</summary>
    public long Stream { get; set; }
}

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#push-notifications">pusher</see>: where to send push notifications of a user, usually a push gateway that wakes a phone app.</summary>
public sealed class PusherRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>App the pusher is for, e.g. <c>io.element.android</c>.</summary>
    public required string AppId { get; set; }
    /// <summary>Key the gateway uses to reach the device, e.g. a Firebase token.</summary>
    public required string PushKey { get; set; }
    /// <summary>Kind: <c>http</c>, or <c>email</c>, which is stored but not sent.</summary>
    public required string Kind { get; set; }
    /// <summary>Name of the app for people.</summary>
    public required string AppDisplayName { get; set; }
    /// <summary>Name of the device for people.</summary>
    public required string DeviceDisplayName { get; set; }
    /// <summary>Profile tag of the device, or null.</summary>
    public string? ProfileTag { get; set; }
    /// <summary>Preferred language, e.g. <c>en</c>.</summary>
    public required string Lang { get; set; }
    /// <summary>The pusher's <c>data</c> as JSON: the gateway <c>url</c> and <c>format</c>, and whatever else the app put there.</summary>
    public required string DataJson { get; set; }
    /// <summary>Device that set the pusher; logging it out deletes the pusher.</summary>
    public string? DeviceId { get; set; }
    /// <summary>When it was set, in milliseconds since the Unix epoch, sent to the gateway as <c>pushkey_ts</c>.</summary>
    public long CreatedTs { get; set; }
}

/// <summary>A named counter of the server, such as the last <see cref="Sync.ServerStream"/> position.</summary>
public sealed class CounterRow
{
    /// <summary>Counter name; primary key.</summary>
    public required string Name { get; set; }
    /// <summary>Value.</summary>
    public long Value { get; set; }
}

/// <summary>A file in the media repository, uploaded by a local user; the bytes are stored in the media directory.</summary>
public sealed class MediaRow
{
    /// <summary>Media ID, the last part of its <c>mxc://</c> URI; primary key.</summary>
    public required string MediaId { get; set; }
    /// <summary>Uploader.</summary>
    public required string UserId { get; set; }
    /// <summary>Content type given at upload.</summary>
    public required string ContentType { get; set; }
    /// <summary>File name given at upload.</summary>
    public string? FileName { get; set; }
    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }
    /// <summary>Upload time in milliseconds since the Unix epoch.</summary>
    public long CreatedTs { get; set; }
}

/// <summary>A generated thumbnail of a <see cref="MediaRow"/>, stored next to it.</summary>
public sealed class ThumbnailRow
{
    /// <summary>Media.</summary>
    public required string MediaId { get; set; }
    /// <summary>Width of the requested size.</summary>
    public int Width { get; set; }
    /// <summary>Height of the requested size.</summary>
    public int Height { get; set; }
    /// <summary><c>crop</c> or <c>scale</c>.</summary>
    public required string Method { get; set; }
    /// <summary>Content type of the thumbnail.</summary>
    public required string ContentType { get; set; }
    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }
}

/// <summary>The signed <see href="https://spec.matrix.org/latest/client-server-api/#device-keys">device keys</see> of a device, without signatures others uploaded later.</summary>
public sealed class DeviceKeysRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Device.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Device keys JSON as uploaded.</summary>
    public required string Json { get; set; }
}

/// <summary>An unclaimed one-time key of a device.</summary>
public sealed class OneTimeKeyRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Device.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Algorithm and key ID, e.g. <c>signed_curve25519:AAAAHg</c>.</summary>
    public required string KeyId { get; set; }
    /// <summary>Algorithm, e.g. <c>signed_curve25519</c>.</summary>
    public required string Algorithm { get; set; }
    /// <summary>Key JSON: a signed key object, or a string for unsigned keys.</summary>
    public required string Json { get; set; }
}

/// <summary>The fallback key of a device for one algorithm, given out when its one-time keys run out.</summary>
public sealed class FallbackKeyRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Device.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Algorithm, e.g. <c>signed_curve25519</c>.</summary>
    public required string Algorithm { get; set; }
    /// <summary>Algorithm and key ID.</summary>
    public required string KeyId { get; set; }
    /// <summary>Key JSON.</summary>
    public required string Json { get; set; }
    /// <summary>Whether it has been claimed since it was uploaded.</summary>
    public bool Used { get; set; }
}

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#cross-signing">cross-signing</see> key of a user.</summary>
public sealed class CrossSigningKeyRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary><c>master</c>, <c>self_signing</c> or <c>user_signing</c>.</summary>
    public required string Usage { get; set; }
    /// <summary>Unpadded base64 Ed25519 public key.</summary>
    public required string PublicKey { get; set; }
    /// <summary>Key JSON as uploaded.</summary>
    public required string Json { get; set; }
}

/// <summary>A signature uploaded later for a device key or a master key, kept apart so each user sees only the signatures meant for them.</summary>
public sealed class KeySignatureRow
{
    /// <summary>Owner of the signed key.</summary>
    public required string TargetUserId { get; set; }
    /// <summary>Signed key: a device ID, or the public key of a master key.</summary>
    public required string TargetKeyId { get; set; }
    /// <summary>Signing user.</summary>
    public required string SignerUserId { get; set; }
    /// <summary>Signing key, e.g. <c>ed25519:DEVICEID</c>.</summary>
    public required string SignerKeyId { get; set; }
    /// <summary>Unpadded base64 signature.</summary>
    public required string Signature { get; set; }
}

/// <summary>The latest change of a user's devices or cross-signing keys, which others' syncs report in <c>device_lists.changed</c>.</summary>
public sealed class DeviceListChangeRow
{
    /// <summary>User whose keys changed; primary key.</summary>
    public required string UserId { get; set; }
    /// <summary>Stream position of the latest change.</summary>
    public long Stream { get; set; }
}

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#send-to-device-messaging">to-device message</see> waiting for its device's next sync.</summary>
public sealed class ToDeviceRow
{
    /// <summary>Row ID; primary key.</summary>
    public long Id { get; set; }
    /// <summary>Recipient.</summary>
    public required string UserId { get; set; }
    /// <summary>Recipient device.</summary>
    public required string DeviceId { get; set; }
    /// <summary>Sender.</summary>
    public required string Sender { get; set; }
    /// <summary>Event type.</summary>
    public required string Type { get; set; }
    /// <summary>Content JSON.</summary>
    public required string Json { get; set; }
    /// <summary>Stream position of the send; deleted once a sync from a later token proves delivery.</summary>
    public long Stream { get; set; }
}

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#server-side-key-backups">key backup</see> version of a user.</summary>
public sealed class KeyBackupVersionRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Version number, increasing per user.</summary>
    public long Version { get; set; }
    /// <summary>Backup algorithm.</summary>
    public required string Algorithm { get; set; }
    /// <summary>Algorithm-specific auth data JSON, such as the public key.</summary>
    public required string AuthDataJson { get; set; }
    /// <summary>Tag that changes with every change of the backed-up keys.</summary>
    public long Etag { get; set; }
    /// <summary>Whether the version was deleted; its number is not reused.</summary>
    public bool Deleted { get; set; }
}

/// <summary>A backed-up room key of one session.</summary>
public sealed class RoomKeyRow
{
    /// <summary>Owner.</summary>
    public required string UserId { get; set; }
    /// <summary>Backup version.</summary>
    public long Version { get; set; }
    /// <summary>Room.</summary>
    public required string RoomId { get; set; }
    /// <summary>Megolm session ID.</summary>
    public required string SessionId { get; set; }
    /// <summary>Key data JSON, with the encrypted session data.</summary>
    public required string Json { get; set; }
    /// <summary>First message index the key decrypts; lower is better.</summary>
    public long FirstMessageIndex { get; set; }
    /// <summary>Times the key was forwarded; lower is better.</summary>
    public long ForwardedCount { get; set; }
    /// <summary>Whether the key came from a verified device.</summary>
    public bool IsVerified { get; set; }
}

/// <summary>An invite from another server to a room not on this server yet, with the room's stripped state; joining the room or rejecting the invite ends it.</summary>
public sealed class RemoteInviteRow
{
    /// <summary>Invited local user.</summary>
    public required string UserId { get; set; }
    /// <summary>Room on the other server.</summary>
    public required string RoomId { get; set; }
    /// <summary>Invite event ID.</summary>
    public required string EventId { get; set; }
    /// <summary>Who invited, a user of the other server.</summary>
    public required string Sender { get; set; }
    /// <summary>Room version.</summary>
    public required string RoomVersion { get; set; }
    /// <summary>The invite PDU, signed by both servers.</summary>
    public required string EventJson { get; set; }
    /// <summary>Stripped state events the inviting server sent, as a JSON array.</summary>
    public required string StrippedStateJson { get; set; }
    /// <summary><c>invite</c>, or <c>leave</c> once rejected.</summary>
    public required string Membership { get; set; }
    /// <summary>Stream position of the last change, for sync.</summary>
    public long Stream { get; set; }
}
