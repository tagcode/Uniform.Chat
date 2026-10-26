namespace Uniform.Matrix.Protocol.Crypto;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>A device of a user, from verified device keys.</summary>
public sealed record DeviceInfo
{
    /// <summary>Owner.</summary>
    public required string UserId { get; init; }
    /// <summary>Device ID.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Curve25519 identity key.</summary>
    public required string Curve25519Key { get; init; }
    /// <summary>Ed25519 signing key, the fingerprint.</summary>
    public required string Ed25519Key { get; init; }
    /// <summary>Display name, e.g. the client name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Self-signing key that validly signed this device's keys, when any; see <see cref="OlmMachine.GetDeviceTrust"/>.</summary>
    public string? SignedBySelfSigningKey { get; init; }
    /// <summary>Whether this device was verified directly, e.g. by comparing emoji; kept while its key stays the same.</summary>
    public bool Verified { get; init; }
}

/// <summary>A user's published <see href="https://spec.matrix.org/latest/client-server-api/#cross-signing">cross-signing</see> keys, checked against each other.</summary>
public sealed record UserIdentity
{
    /// <summary>Owner.</summary>
    public required string UserId { get; init; }
    /// <summary>Master public key, the user's identity.</summary>
    public required string MasterKey { get; init; }
    /// <summary>Self-signing public key, when validly signed by the master key.</summary>
    public string? SelfSigningKey { get; init; }
    /// <summary>User-signing public key, published only to its owner, when validly signed by the master key.</summary>
    public string? UserSigningKey { get; init; }
    /// <summary>Our user-signing key, when it validly signed this master key.</summary>
    public string? SignedByUserSigningKey { get; init; }
    /// <summary>Whether this master key was verified directly, from secret storage or by comparing emoji; kept while the key stays the same.</summary>
    public bool Trusted { get; init; }
}

/// <summary>Stored Olm account.</summary>
public sealed record AccountRecord
{
    /// <summary>Encrypted account pickle.</summary>
    public required string Pickle { get; init; }
    /// <summary>Whether the device keys have been uploaded.</summary>
    public bool DeviceKeysUploaded { get; init; }
}

/// <summary>Stored Olm session with another device.</summary>
public sealed record OlmSessionRecord
{
    /// <summary>Curve25519 key of the other device.</summary>
    public required string SenderKey { get; init; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; init; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; init; }
    /// <summary>Last use; the newest session is used for sending.</summary>
    public DateTimeOffset LastUsed { get; init; }
}

/// <summary>Stored Megolm session for decrypting one sender's room messages.</summary>
public sealed record InboundGroupSessionRecord
{
    /// <summary>Room the session belongs to.</summary>
    public required string RoomId { get; init; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; init; }
    /// <summary>Curve25519 key of the device the key came from over Olm.</summary>
    public required string SenderKey { get; init; }
    /// <summary>Ed25519 key the sending device claimed in the Olm payload.</summary>
    public required string SigningKey { get; init; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; init; }
    /// <summary>Whether the session is in the current key backup.</summary>
    public bool BackedUp { get; init; }
    /// <summary>Whether the key came from a backup or another device instead of its sender, so the sender is not proven.</summary>
    public bool Imported { get; init; }
}

/// <summary>Stored Megolm session for sending to a room.</summary>
public sealed record OutboundGroupSessionRecord
{
    /// <summary>Room the session is for.</summary>
    public required string RoomId { get; init; }
    /// <summary>Session ID.</summary>
    public required string SessionId { get; init; }
    /// <summary>Encrypted session pickle.</summary>
    public required string Pickle { get; init; }
    /// <summary>Creation time, for rotation by age.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Messages sent, for rotation by count.</summary>
    public int MessageCount { get; init; }
    /// <summary>Devices the key was shared with, as <c>user|device</c>.</summary>
    public IReadOnlySet<string> SharedWith { get; init; } = new HashSet<string>();
}

/// <summary>Stored device list of a user.</summary>
public sealed record DeviceListRecord
{
    /// <summary>User whose devices these are.</summary>
    public required string UserId { get; init; }
    /// <summary>Verified devices.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; init; } = [];
    /// <summary>Whether the list must be queried again before use, e.g. after <see cref="DeviceLists.Changed"/>.</summary>
    public bool Outdated { get; init; }
    /// <summary>Cross-signing keys, when the user has set them up.</summary>
    public UserIdentity? Identity { get; init; }
}

/// <summary>Persistent storage of encryption state; pickles are encrypted, so the store itself need not be.</summary>
public interface ICryptoStore
{
    /// <summary>Return the account, or null before the first run.</summary>
    Task<AccountRecord?> LoadAccountAsync(CancellationToken cancellationToken);
    /// <summary>Save the account.</summary>
    Task SaveAccountAsync(AccountRecord account, CancellationToken cancellationToken);
    /// <summary>Return Olm sessions with the device of <paramref name="senderKey"/>.</summary>
    Task<IReadOnlyList<OlmSessionRecord>> LoadOlmSessionsAsync(string senderKey, CancellationToken cancellationToken);
    /// <summary>Add or replace an Olm session.</summary>
    Task SaveOlmSessionAsync(OlmSessionRecord session, CancellationToken cancellationToken);
    /// <summary>Return an inbound Megolm session, or null.</summary>
    Task<InboundGroupSessionRecord?> LoadInboundGroupSessionAsync(string roomId, string sessionId, CancellationToken cancellationToken);
    /// <summary>Add or replace an inbound Megolm session.</summary>
    Task SaveInboundGroupSessionAsync(InboundGroupSessionRecord session, CancellationToken cancellationToken);
    /// <summary>Return the outbound Megolm session of a room, or null.</summary>
    Task<OutboundGroupSessionRecord?> LoadOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken);
    /// <summary>Add or replace the outbound Megolm session of a room.</summary>
    Task SaveOutboundGroupSessionAsync(OutboundGroupSessionRecord session, CancellationToken cancellationToken);
    /// <summary>Remove the outbound Megolm session of a room, forcing a new one.</summary>
    Task DeleteOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken);
    /// <summary>Return all stored device lists.</summary>
    Task<IReadOnlyList<DeviceListRecord>> LoadDeviceListsAsync(CancellationToken cancellationToken);
    /// <summary>Add or replace a user's device list.</summary>
    Task SaveDeviceListAsync(DeviceListRecord deviceList, CancellationToken cancellationToken);
    /// <summary>Return up to <paramref name="limit"/> inbound sessions not yet in the key backup.</summary>
    Task<IReadOnlyList<InboundGroupSessionRecord>> LoadSessionsToBackUpAsync(int limit, CancellationToken cancellationToken);
    /// <summary>Mark every inbound session as not backed up, e.g. for a new backup version.</summary>
    Task ResetBackedUpAsync(CancellationToken cancellationToken);
    /// <summary>Return a named value, or null; secrets in it are encrypted by the caller.</summary>
    Task<string?> LoadValueAsync(string name, CancellationToken cancellationToken);
    /// <summary>Set a named value, or remove it when <paramref name="value"/> is null.</summary>
    Task SaveValueAsync(string name, string? value, CancellationToken cancellationToken);
}

/// <summary><see cref="ICryptoStore"/> in memory, for tests and short-lived clients.</summary>
public sealed class InMemoryCryptoStore : ICryptoStore
{
    private readonly ConcurrentDictionary<(string, string), OlmSessionRecord> olmSessions = new();
    private readonly ConcurrentDictionary<(string, string), InboundGroupSessionRecord> inbound = new();
    private readonly ConcurrentDictionary<string, OutboundGroupSessionRecord> outbound = new();
    private readonly ConcurrentDictionary<string, DeviceListRecord> deviceLists = new();
    private readonly ConcurrentDictionary<string, string> values = new();
    private AccountRecord? account;

    /// <summary>Return the account.</summary>
    public Task<AccountRecord?> LoadAccountAsync(CancellationToken cancellationToken) => Task.FromResult(account);
    /// <summary>Save the account.</summary>
    public Task SaveAccountAsync(AccountRecord value, CancellationToken cancellationToken) { account = value; return Task.CompletedTask; }
    /// <summary>Return sessions with a device.</summary>
    public Task<IReadOnlyList<OlmSessionRecord>> LoadOlmSessionsAsync(string senderKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OlmSessionRecord>>([.. olmSessions.Values.Where(s => s.SenderKey == senderKey)]);
    /// <summary>Save a session.</summary>
    public Task SaveOlmSessionAsync(OlmSessionRecord session, CancellationToken cancellationToken) { olmSessions[(session.SenderKey, session.SessionId)] = session; return Task.CompletedTask; }
    /// <summary>Return an inbound session.</summary>
    public Task<InboundGroupSessionRecord?> LoadInboundGroupSessionAsync(string roomId, string sessionId, CancellationToken cancellationToken) => Task.FromResult(inbound.GetValueOrDefault((roomId, sessionId)));
    /// <summary>Save an inbound session.</summary>
    public Task SaveInboundGroupSessionAsync(InboundGroupSessionRecord session, CancellationToken cancellationToken) { inbound[(session.RoomId, session.SessionId)] = session; return Task.CompletedTask; }
    /// <summary>Return a room's outbound session.</summary>
    public Task<OutboundGroupSessionRecord?> LoadOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken) => Task.FromResult(outbound.GetValueOrDefault(roomId));
    /// <summary>Save a room's outbound session.</summary>
    public Task SaveOutboundGroupSessionAsync(OutboundGroupSessionRecord session, CancellationToken cancellationToken) { outbound[session.RoomId] = session; return Task.CompletedTask; }
    /// <summary>Remove a room's outbound session.</summary>
    public Task DeleteOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken) { outbound.TryRemove(roomId, out _); return Task.CompletedTask; }
    /// <summary>Return all device lists.</summary>
    public Task<IReadOnlyList<DeviceListRecord>> LoadDeviceListsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceListRecord>>([.. deviceLists.Values]);
    /// <summary>Save a device list.</summary>
    public Task SaveDeviceListAsync(DeviceListRecord deviceList, CancellationToken cancellationToken) { deviceLists[deviceList.UserId] = deviceList; return Task.CompletedTask; }
    /// <summary>Return sessions not backed up.</summary>
    public Task<IReadOnlyList<InboundGroupSessionRecord>> LoadSessionsToBackUpAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InboundGroupSessionRecord>>([.. inbound.Values.Where(s => !s.BackedUp).Take(limit)]);
    /// <summary>Mark all sessions not backed up.</summary>
    public Task ResetBackedUpAsync(CancellationToken cancellationToken) { foreach (((string, string) key, InboundGroupSessionRecord session) in inbound) inbound[key] = session with { BackedUp = false }; return Task.CompletedTask; }
    /// <summary>Return a value.</summary>
    public Task<string?> LoadValueAsync(string name, CancellationToken cancellationToken) => Task.FromResult(values.GetValueOrDefault(name));
    /// <summary>Set or remove a value.</summary>
    public Task SaveValueAsync(string name, string? value, CancellationToken cancellationToken) { if (value is null) values.TryRemove(name, out _); else values[name] = value; return Task.CompletedTask; }
}

/// <summary>Network calls the <see cref="OlmMachine"/> needs; <c>MatrixKeyTransport</c> in Uniform.Matrix.Client sends them to a homeserver.</summary>
public interface IKeyTransport
{
    /// <summary>Upload signed keys, see <c>MatrixClient.UploadKeysAsync</c>.</summary>
    Task<KeysUploadResponse> UploadKeysAsync(JsonObject? deviceKeys, JsonObject? oneTimeKeys, JsonObject? fallbackKeys, CancellationToken cancellationToken);
    /// <summary>Query device keys, see <c>MatrixClient.QueryKeysAsync</c>.</summary>
    Task<KeysQueryResponse> QueryKeysAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken);
    /// <summary>Claim one-time keys, see <c>MatrixClient.ClaimKeysAsync</c>.</summary>
    Task<KeysClaimResponse> ClaimKeysAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> devices, CancellationToken cancellationToken);
    /// <summary>Send to-device messages, see <c>MatrixClient.SendToDeviceAsync</c>.</summary>
    Task SendToDeviceAsync(string eventType, IReadOnlyDictionary<string, Dictionary<string, object>> messages, CancellationToken cancellationToken);
    /// <summary>Upload cross-signing keys, see <c>MatrixClient.UploadCrossSigningKeysAsync</c>.</summary>
    Task UploadCrossSigningKeysAsync(JsonObject masterKey, JsonObject selfSigningKey, JsonObject userSigningKey, string? password, CancellationToken cancellationToken);
    /// <summary>Upload signatures, see <c>MatrixClient.UploadSignaturesAsync</c>.</summary>
    Task UploadSignaturesAsync(IReadOnlyDictionary<string, Dictionary<string, JsonObject>> signatures, CancellationToken cancellationToken);
    /// <summary>Return global account data of <paramref name="type"/>, or null.</summary>
    Task<JsonObject?> GetAccountDataAsync(string type, CancellationToken cancellationToken);
    /// <summary>Set global account data.</summary>
    Task SetAccountDataAsync(string type, JsonObject content, CancellationToken cancellationToken);
    /// <summary>Return the current key backup, see <c>MatrixClient.GetKeyBackupVersionAsync</c>.</summary>
    Task<KeyBackupVersion?> GetKeyBackupVersionAsync(CancellationToken cancellationToken);
    /// <summary>Create a key backup version, see <c>MatrixClient.CreateKeyBackupVersionAsync</c>.</summary>
    Task<string> CreateKeyBackupVersionAsync(string algorithm, JsonObject authData, CancellationToken cancellationToken);
    /// <summary>Upload backed-up keys, see <c>MatrixClient.UploadRoomKeysAsync</c>.</summary>
    Task UploadRoomKeysAsync(string version, KeysBackup keys, CancellationToken cancellationToken);
    /// <summary>Download backed-up keys, see <c>MatrixClient.GetRoomKeysAsync</c>.</summary>
    Task<KeysBackup> GetRoomKeysAsync(string version, CancellationToken cancellationToken);
}
