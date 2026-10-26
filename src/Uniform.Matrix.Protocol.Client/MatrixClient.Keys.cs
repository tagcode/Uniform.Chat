namespace Uniform.Matrix.Protocol.Client;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Encryption keys and to-device messages.
public sealed partial class MatrixClient
{
    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysupload">Uploads</see> signed device keys, one-time keys and fallback keys; any may be null.</summary>
    /// <param name="deviceKeys">Signed device keys, uploaded once per device.</param>
    /// <param name="oneTimeKeys">Signed one-time keys by <c>signed_curve25519:id</c>.</param>
    /// <param name="fallbackKeys">Signed fallback keys by <c>signed_curve25519:id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<KeysUploadResponse> UploadKeysAsync(JsonObject? deviceKeys, JsonObject? oneTimeKeys, JsonObject? fallbackKeys, CancellationToken cancellationToken = default) =>
        SendAsync<KeysUploadResponse>(HttpMethod.Post, "_matrix/client/v3/keys/upload", new { device_keys = deviceKeys, one_time_keys = oneTimeKeys, fallback_keys = fallbackKeys }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysquery">Queries</see> all device keys of <paramref name="userIds"/>.</summary>
    public Task<KeysQueryResponse> QueryKeysAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default) =>
        SendAsync<KeysQueryResponse>(HttpMethod.Post, "_matrix/client/v3/keys/query", new { device_keys = userIds.Distinct().ToDictionary(u => u, _ => Array.Empty<string>()) }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysclaim">Claims</see> one <see cref="EncryptionAlgorithms.SignedCurve25519"/> key for each user and device in <paramref name="devices"/>.</summary>
    public Task<KeysClaimResponse> ClaimKeysAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> devices, CancellationToken cancellationToken = default) =>
        SendAsync<KeysClaimResponse>(HttpMethod.Post, "_matrix/client/v3/keys/claim", new { one_time_keys = devices.ToDictionary(d => d.Key, d => d.Value.ToDictionary(id => id, _ => EncryptionAlgorithms.SignedCurve25519)) }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3sendtodeviceeventtypetxnid">Sends</see> to-device messages, content by user ID then device ID.</summary>
    /// <param name="eventType">Event type, e.g. <see cref="EventTypes.RoomEncrypted"/>.</param>
    /// <param name="messages">Content by user ID, then device ID or <c>*</c> for all devices.</param>
    /// <param name="transactionId">Reuse when retrying the same send; generated when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendToDeviceAsync(string eventType, IReadOnlyDictionary<string, Dictionary<string, object>> messages, string? transactionId = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/sendToDevice/{Escape(eventType)}/{Escape(transactionId ?? NewTransactionId())}", new { messages }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysdevice_signingupload">Uploads</see> cross-signing public keys; the server may ask for the password, and refuses to replace keys without it.</summary>
    /// <param name="masterKey">Master key, signed by the device.</param>
    /// <param name="selfSigningKey">Self-signing key, signed by the master key.</param>
    /// <param name="userSigningKey">User-signing key, signed by the master key.</param>
    /// <param name="password">Password for the auth challenge; when null, <see cref="UserInteractiveAuthException"/> is thrown so the caller can ask for it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task UploadCrossSigningKeysAsync(JsonObject masterKey, JsonObject selfSigningKey, JsonObject userSigningKey, string? password, CancellationToken cancellationToken = default) =>
        SendWithPasswordAuthAsync(HttpMethod.Post, "_matrix/client/v3/keys/device_signing/upload", auth => new { master_key = masterKey, self_signing_key = selfSigningKey, user_signing_key = userSigningKey, auth }, password, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keyssignaturesupload">Uploads signatures</see> of devices or master keys: user ID, then device ID or public key, then the signed object.</summary>
    public Task<JsonElement> UploadSignaturesAsync(IReadOnlyDictionary<string, Dictionary<string, JsonObject>> signatures, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "_matrix/client/v3/keys/signatures/upload", signatures, authenticated: true, cancellationToken);

    /// <summary>Return the current <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3room_keysversion">key backup version</see>, or null when there is none.</summary>
    public async Task<KeyBackupVersion?> GetKeyBackupVersionAsync(CancellationToken cancellationToken = default)
    {
        // Missing backup is an error response
        try
        {
            return await SendAsync<KeyBackupVersion>(HttpMethod.Get, "_matrix/client/v3/room_keys/version", body: null, authenticated: true, cancellationToken);
        }
        catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
        {
            // Done; no backup
            return null;
        }
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3room_keysversion">Creates</see> a key backup version, which becomes current; returns its version ID.</summary>
    public async Task<string> CreateKeyBackupVersionAsync(string algorithm, JsonObject authData, CancellationToken cancellationToken = default)
    {
        // Create
        JsonElement response = await SendAsync<JsonElement>(HttpMethod.Post, "_matrix/client/v3/room_keys/version", new { algorithm, auth_data = authData }, authenticated: true, cancellationToken);
        // Return version
        return response.GetProperty("version").GetString()!;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3room_keyskeys">Uploads</see> session keys to backup <paramref name="version"/>; the server keeps the better of old and new.</summary>
    public Task UploadRoomKeysAsync(string version, KeysBackup keys, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/room_keys/keys?version={Escape(version)}", keys, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3room_keyskeys">Downloads</see> all session keys of backup <paramref name="version"/>.</summary>
    public Task<KeysBackup> GetRoomKeysAsync(string version, CancellationToken cancellationToken = default) =>
        SendAsync<KeysBackup>(HttpMethod.Get, $"_matrix/client/v3/room_keys/keys?version={Escape(version)}", body: null, authenticated: true, cancellationToken);
}
