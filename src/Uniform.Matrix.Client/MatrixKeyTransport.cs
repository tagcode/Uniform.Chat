namespace Uniform.Matrix.Client;

using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary><see cref="IKeyTransport"/> over a logged-in <see cref="MatrixClient"/>.</summary>
public sealed class MatrixKeyTransport(MatrixClient client) : IKeyTransport
{
    /// <summary>Upload keys.</summary>
    public Task<KeysUploadResponse> UploadKeysAsync(JsonObject? deviceKeys, JsonObject? oneTimeKeys, JsonObject? fallbackKeys, CancellationToken cancellationToken) => client.UploadKeysAsync(deviceKeys, oneTimeKeys, fallbackKeys, cancellationToken);
    /// <summary>Query device keys.</summary>
    public Task<KeysQueryResponse> QueryKeysAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken) => client.QueryKeysAsync(userIds, cancellationToken);
    /// <summary>Claim one-time keys.</summary>
    public Task<KeysClaimResponse> ClaimKeysAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> devices, CancellationToken cancellationToken) => client.ClaimKeysAsync(devices, cancellationToken);
    /// <summary>Send to-device messages.</summary>
    public Task SendToDeviceAsync(string eventType, IReadOnlyDictionary<string, Dictionary<string, object>> messages, CancellationToken cancellationToken) => client.SendToDeviceAsync(eventType, messages, cancellationToken: cancellationToken);
    /// <summary>Upload cross-signing keys.</summary>
    public Task UploadCrossSigningKeysAsync(JsonObject masterKey, JsonObject selfSigningKey, JsonObject userSigningKey, string? password, CancellationToken cancellationToken) => client.UploadCrossSigningKeysAsync(masterKey, selfSigningKey, userSigningKey, password, cancellationToken);
    /// <summary>Upload signatures.</summary>
    public Task UploadSignaturesAsync(IReadOnlyDictionary<string, Dictionary<string, JsonObject>> signatures, CancellationToken cancellationToken) => client.UploadSignaturesAsync(signatures, cancellationToken);
    /// <summary>Return account data.</summary>
    public Task<JsonObject?> GetAccountDataAsync(string type, CancellationToken cancellationToken) => client.TryGetAccountDataAsync<JsonObject>(type, cancellationToken);
    /// <summary>Set account data.</summary>
    public Task SetAccountDataAsync(string type, JsonObject content, CancellationToken cancellationToken) => client.SetAccountDataAsync(type, content, cancellationToken);
    /// <summary>Return the current key backup.</summary>
    public Task<KeyBackupVersion?> GetKeyBackupVersionAsync(CancellationToken cancellationToken) => client.GetKeyBackupVersionAsync(cancellationToken);
    /// <summary>Create a key backup version.</summary>
    public Task<string> CreateKeyBackupVersionAsync(string algorithm, JsonObject authData, CancellationToken cancellationToken) => client.CreateKeyBackupVersionAsync(algorithm, authData, cancellationToken);
    /// <summary>Upload backed-up keys.</summary>
    public Task UploadRoomKeysAsync(string version, KeysBackup keys, CancellationToken cancellationToken) => client.UploadRoomKeysAsync(version, keys, cancellationToken);
    /// <summary>Download backed-up keys.</summary>
    public Task<KeysBackup> GetRoomKeysAsync(string version, CancellationToken cancellationToken) => client.GetRoomKeysAsync(version, cancellationToken);
}
