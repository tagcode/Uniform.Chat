namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>Encryption algorithm names.</summary>
public static class EncryptionAlgorithms
{
    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#molmv1curve25519-aes-sha2">Olm</see>, for to-device messages.</summary>
    public const string Olm = "m.olm.v1.curve25519-aes-sha2";
    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#mmegolmv1aes-sha2">Megolm</see>, for room messages.</summary>
    public const string Megolm = "m.megolm.v1.aes-sha2";
    /// <summary>Algorithm name of signed one-time and fallback keys.</summary>
    public const string SignedCurve25519 = "signed_curve25519";
}

/// <summary>Signed <see href="https://spec.matrix.org/latest/client-server-api/#definition-devicekeys">device keys</see> as uploaded and queried.</summary>
public sealed record DeviceKeys
{
    /// <summary>Owner.</summary>
    public required string UserId { get; init; }
    /// <summary>Device ID.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Supported algorithms.</summary>
    public IReadOnlyList<string> Algorithms { get; init; } = [];
    /// <summary>Public keys by <c>algorithm:device_id</c>, e.g. <c>curve25519:ABC</c>.</summary>
    public IReadOnlyDictionary<string, string> Keys { get; init; } = new Dictionary<string, string>();
    /// <summary>Signatures by user ID, then key ID.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Signatures { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    /// <summary>Unsigned data such as the display name.</summary>
    public JsonObject? Unsigned { get; init; }

    /// <summary>Curve25519 identity key, or null.</summary>
    [JsonIgnore]
    public string? Curve25519Key => Keys.GetValueOrDefault($"curve25519:{DeviceId}");
    /// <summary>Ed25519 signing key, i.e. the fingerprint, or null.</summary>
    [JsonIgnore]
    public string? Ed25519Key => Keys.GetValueOrDefault($"ed25519:{DeviceId}");
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysupload">POST /_matrix/client/v3/keys/upload</see>.</summary>
public sealed record KeysUploadResponse
{
    /// <summary>Unclaimed one-time keys on the server by algorithm.</summary>
    public IReadOnlyDictionary<string, int> OneTimeKeyCounts { get; init; } = new Dictionary<string, int>();
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysquery">POST /_matrix/client/v3/keys/query</see>.</summary>
public sealed record KeysQueryResponse
{
    /// <summary>Device keys by user ID, then device ID; kept raw so signatures can be checked over the exact fields.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonObject>> DeviceKeys { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, JsonObject>>();
    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#cross-signing">Cross-signing</see> master keys by user ID, raw like <see cref="DeviceKeys"/>.</summary>
    public IReadOnlyDictionary<string, JsonObject> MasterKeys { get; init; } = new Dictionary<string, JsonObject>();
    /// <summary>Self-signing keys by user ID, signed by the master key; they sign the user's devices.</summary>
    public IReadOnlyDictionary<string, JsonObject> SelfSigningKeys { get; init; } = new Dictionary<string, JsonObject>();
    /// <summary>User-signing key of the requesting user only; it signs other users' master keys.</summary>
    public IReadOnlyDictionary<string, JsonObject> UserSigningKeys { get; init; } = new Dictionary<string, JsonObject>();
    /// <summary>Servers that could not be reached, with errors.</summary>
    public IReadOnlyDictionary<string, JsonElement> Failures { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#cross-signing">cross-signing key</see>: master, self-signing or user-signing.</summary>
public sealed record CrossSigningKey
{
    /// <summary>Owner.</summary>
    public string? UserId { get; init; }
    /// <summary><c>master</c>, <c>self_signing</c> or <c>user_signing</c>.</summary>
    public IReadOnlyList<string> Usage { get; init; } = [];
    /// <summary>The one public key, by <c>ed25519:</c> plus the key itself.</summary>
    public IReadOnlyDictionary<string, string> Keys { get; init; } = new Dictionary<string, string>();
    /// <summary>Signatures by user ID, then key ID.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Signatures { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>The public key, base64.</summary>
    [JsonIgnore]
    public string? PublicKey => Keys.Count == 1 ? Keys.Values.First() : null;
}

/// <summary>Usages of <see cref="CrossSigningKey.Usage"/>.</summary>
public static class CrossSigningUsages
{
    /// <summary>The user's identity; signs the other two.</summary>
    public const string Master = "master";
    /// <summary>Signs the user's own devices.</summary>
    public const string SelfSigning = "self_signing";
    /// <summary>Signs other users' master keys.</summary>
    public const string UserSigning = "user_signing";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#server-side-key-backups">Key backup</see> version from <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3room_keysversion">GET /room_keys/version</see>.</summary>
public sealed record KeyBackupVersion
{
    /// <summary>Algorithm, see <see cref="KeyBackupAlgorithms"/>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>Algorithm data, e.g. the public key and its signatures.</summary>
    public JsonObject? AuthData { get; init; }
    /// <summary>Version ID.</summary>
    public string? Version { get; init; }
    /// <summary>Number of stored keys.</summary>
    public int? Count { get; init; }
    /// <summary>Changes when keys are added.</summary>
    public string? Etag { get; init; }
}

/// <summary>Algorithms of <see cref="KeyBackupVersion.Algorithm"/>.</summary>
public static class KeyBackupAlgorithms
{
    /// <summary>Session keys encrypted to a Curve25519 public key.</summary>
    public const string MegolmV1 = "m.megolm_backup.v1.curve25519-aes-sha2";
}

/// <summary>One backed-up session key.</summary>
public sealed record KeyBackupData
{
    /// <summary>First message index the key decrypts.</summary>
    public int FirstMessageIndex { get; init; }
    /// <summary>How many times the key was forwarded between devices.</summary>
    public int ForwardedCount { get; init; }
    /// <summary>Whether the uploading device trusted the key's sender.</summary>
    public bool IsVerified { get; init; }
    /// <summary>Encrypted session, as defined by the algorithm.</summary>
    public JsonObject? SessionData { get; init; }
}

/// <summary>Backed-up keys of one room, by session ID.</summary>
public sealed record RoomKeyBackup
{
    /// <summary>Keys by session ID.</summary>
    public Dictionary<string, KeyBackupData> Sessions { get; init; } = [];
}

/// <summary>Backed-up keys by room ID, as uploaded and downloaded by <see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3room_keyskeys">/room_keys/keys</see>.</summary>
public sealed record KeysBackup
{
    /// <summary>Rooms by room ID.</summary>
    public Dictionary<string, RoomKeyBackup> Rooms { get; init; } = [];
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysclaim">POST /_matrix/client/v3/keys/claim</see>.</summary>
public sealed record KeysClaimResponse
{
    /// <summary>Claimed keys by user ID, device ID, then key ID; each a signed key object.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonObject>>> OneTimeKeys { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonObject>>>();
    /// <summary>Servers that could not be reached, with errors.</summary>
    public IReadOnlyDictionary<string, JsonElement> Failures { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomencrypted">m.room.encrypted</see>, both Olm and Megolm.</summary>
public sealed record EncryptedContent
{
    /// <summary>Algorithm, see <see cref="EncryptionAlgorithms"/>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>Megolm: ciphertext string. Olm: object keyed by recipient Curve25519 key, each with <c>type</c> and <c>body</c>.</summary>
    public JsonElement Ciphertext { get; init; }
    /// <summary>Sender's Curve25519 key; required for Olm, deprecated for Megolm.</summary>
    public string? SenderKey { get; init; }
    /// <summary>Sender's device; deprecated for Megolm.</summary>
    public string? DeviceId { get; init; }
    /// <summary>Megolm session ID.</summary>
    public string? SessionId { get; init; }
    /// <summary>Relation, kept unencrypted so servers can aggregate edits and threads.</summary>
    [JsonPropertyName("m.relates_to")]
    public Relation? RelatesTo { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroom_key">m.room_key</see>, received over Olm.</summary>
public sealed record RoomKeyContent
{
    /// <summary>Algorithm, <see cref="EncryptionAlgorithms.Megolm"/>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>Room the session is for.</summary>
    public string? RoomId { get; init; }
    /// <summary>Megolm session ID.</summary>
    public string? SessionId { get; init; }
    /// <summary>Megolm session key.</summary>
    public string? SessionKey { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomencryption">m.room.encryption</see>.</summary>
public sealed record EncryptionContent
{
    /// <summary>Algorithm, <see cref="EncryptionAlgorithms.Megolm"/>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>Rotate the session after this many milliseconds; a week by default.</summary>
    public long? RotationPeriodMs { get; init; }
    /// <summary>Rotate the session after this many messages; 100 by default.</summary>
    public int? RotationPeriodMsgs { get; init; }
}
