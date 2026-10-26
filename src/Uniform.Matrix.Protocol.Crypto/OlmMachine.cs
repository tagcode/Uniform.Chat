namespace Uniform.Matrix.Protocol.Crypto;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#end-to-end-encryption">End-to-end encryption</see> state machine of one device: keys, Olm sessions with other devices and Megolm sessions for rooms.</summary>
/// <remarks>
/// Feed every sync to <see cref="ReceiveSyncAsync"/>, decrypt <c>m.room.encrypted</c> timeline events with <see cref="DecryptRoomEventAsync"/>,
/// and encrypt outgoing events for encrypted rooms with <see cref="EncryptRoomEventAsync"/>. Calls are serialized, so it can be shared between threads.
/// Split into partial files:
/// <list type="bullet">
/// <item><description>OlmMachine.cs: account, key upload and device lists.</description></item>
/// <item><description>OlmMachine.Olm.cs: Olm sessions and to-device messages, including received room keys.</description></item>
/// <item><description>OlmMachine.Megolm.cs: room message encryption, key sharing and decryption.</description></item>
/// <item><description>OlmMachine.CrossSigning.cs: cross-signing keys, trust and recovery from secret storage.</description></item>
/// <item><description>OlmMachine.Backup.cs: server-side key backup.</description></item>
/// <item><description>OlmMachine.KeyRequests.cs: room key and secret requests between the user's own devices.</description></item>
/// <item><description>OlmMachine.Verification.cs: SAS emoji verification of devices.</description></item>
/// </list>
/// </remarks>
/// <example><code><![CDATA[
/// OlmMachine machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), store, client.UserId!, client.DeviceId!, pickleKey);
/// await foreach (SyncResponse sync in client.SyncLoopAsync())
/// {
///     await machine.ReceiveSyncAsync(sync);
///     // decrypt timeline events with machine.DecryptRoomEventAsync(roomId, ev)
/// }
/// ]]></code></example>
public sealed partial class OlmMachine : IDisposable
{
    private readonly IKeyTransport transport;
    private readonly ICryptoStore store;
    private readonly byte[] pickleKey;
    private readonly OlmAccount account;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DeviceListRecord> deviceLists;
    private readonly TimeProvider time;
    private readonly Dictionary<string, string> privateKeys;
    private bool deviceKeysUploaded;

    /// <summary>Wrap loaded state; use <see cref="CreateAsync"/>.</summary>
    private OlmMachine(IKeyTransport transport, ICryptoStore store, string userId, string deviceId, byte[] pickleKey, OlmAccount account, bool deviceKeysUploaded, IEnumerable<DeviceListRecord> deviceLists, Dictionary<string, string> privateKeys, TimeProvider time)
    {
        // Store state
        this.transport = transport;
        this.store = store;
        this.pickleKey = pickleKey;
        this.account = account;
        this.deviceKeysUploaded = deviceKeysUploaded;
        this.deviceLists = deviceLists.ToDictionary(d => d.UserId);
        this.privateKeys = privateKeys;
        this.time = time;
        UserId = userId;
        DeviceId = deviceId;
    }

    /// <summary>Load the device's account from <paramref name="store"/>, or create one on first use.</summary>
    /// <param name="transport">Network calls, usually <c>MatrixKeyTransport</c> in Uniform.Matrix.Client.</param>
    /// <param name="store">Persistent state.</param>
    /// <param name="userId">Logged-in user.</param>
    /// <param name="deviceId">This device.</param>
    /// <param name="pickleKey">32-byte key encrypting stored pickles; keep it secret and stable.</param>
    /// <param name="time">Clock for session rotation; system clock when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<OlmMachine> CreateAsync(IKeyTransport transport, ICryptoStore store, string userId, string deviceId, byte[] pickleKey, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        // Load or create the account
        AccountRecord? stored = await store.LoadAccountAsync(cancellationToken);
        OlmAccount account = stored is null ? OlmAccount.Create() : OlmAccount.FromPickle(stored.Pickle, pickleKey);
        if (stored is null) await store.SaveAccountAsync(new AccountRecord { Pickle = account.Pickle(pickleKey) }, cancellationToken);

        // Load known devices and private keys
        IReadOnlyList<DeviceListRecord> lists = await store.LoadDeviceListsAsync(cancellationToken);
        Dictionary<string, string> privateKeys = [];
        foreach (string name in StoredSecretNames)
        {
            if (await store.LoadValueAsync(SecretValuePrefix + name, cancellationToken) is not string json || JsonSerializer.Deserialize<EncryptedSecret>(json, MatrixJson.Options) is not EncryptedSecret encrypted) continue;
            privateKeys[name] = SecretStorageCrypto.Decrypt(pickleKey, name, encrypted);
        }
        // Return machine
        return new OlmMachine(transport, store, userId, deviceId, pickleKey, account, stored?.DeviceKeysUploaded ?? false, lists, privateKeys, time ?? TimeProvider.System);
    }

    /// <summary>Logged-in user.</summary>
    public string UserId { get; }
    /// <summary>This device.</summary>
    public string DeviceId { get; }
    /// <summary>This device's public identity keys.</summary>
    public IdentityKeys IdentityKeys => account.IdentityKeys;

    /// <summary>Raised with room and session ID when a room key arrives, so events that failed to decrypt can be retried.</summary>
    public event Action<string, string>? RoomKeyReceived;

    /// <summary>Process the encryption parts of a sync: device list changes, to-device messages and the one-time key count; return decrypted to-device events.</summary>
    public async Task<IReadOnlyList<DecryptedToDeviceEvent>> ReceiveSyncAsync(SyncResponse sync, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Changed device lists must be fetched again before the next send
            foreach (string userId in sync.DeviceLists.Changed.Concat(sync.DeviceLists.Left)) await MarkOutdatedAsync(userId, cancellationToken);

            // To-device messages: Olm-encrypted keys and secrets, plain requests and verification
            List<DecryptedToDeviceEvent> decrypted = [];
            foreach (ToDeviceEvent ev in sync.ToDevice.Events)
            {
                if (ev.Type != EventTypes.RoomEncrypted)
                {
                    if (JsonSerializer.Deserialize<JsonObject>(ev.Content) is JsonObject plain) await HandlePlainToDeviceAsync(ev.Type, ev.Sender, plain, cancellationToken);
                    continue;
                }
                if (await DecryptToDeviceAsync(ev, cancellationToken) is not DecryptedToDeviceEvent result) continue;
                await HandleDecryptedToDeviceAsync(result, cancellationToken);
                decrypted.Add(result);
            }

            // Ask own devices for keys that were missing
            await SendKeyRequestsAsync(cancellationToken);

            // Keep one-time keys and the fallback key topped up
            int? count = sync.DeviceOneTimeKeysCount?.GetValueOrDefault(EncryptionAlgorithms.SignedCurve25519);
            bool fallbackUsed = sync.DeviceUnusedFallbackKeyTypes is { } unused && !unused.Contains(EncryptionAlgorithms.SignedCurve25519);
            await UploadKeysIfNeededAsync(count, fallbackUsed, cancellationToken);
            // Return decrypted events
            return decrypted;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Handle an unencrypted to-device event: key and secret requests, and verification.</summary>
    private async Task HandlePlainToDeviceAsync(string type, string sender, JsonObject content, CancellationToken cancellationToken)
    {
        // By type; secrets and keys themselves only ever arrive encrypted
        if (type == EventTypes.RoomKeyRequest) await HandleRoomKeyRequestAsync(sender, content, cancellationToken);
        else if (type == EventTypes.SecretRequest) await HandleSecretRequestAsync(sender, content, cancellationToken);
        else if (type.StartsWith("m.key.verification.", StringComparison.Ordinal)) await HandleVerificationEventAsync(type, sender, content, cancellationToken);
    }

    /// <summary>Handle an Olm-decrypted to-device event: room keys, forwarded keys, secrets and verification.</summary>
    private async Task HandleDecryptedToDeviceAsync(DecryptedToDeviceEvent toDeviceEvent, CancellationToken cancellationToken)
    {
        // By type
        if (toDeviceEvent.Type == EventTypes.RoomKey) await AddRoomKeyAsync(toDeviceEvent, cancellationToken);
        else if (toDeviceEvent.Type == EventTypes.ForwardedRoomKey) await AddForwardedRoomKeyAsync(toDeviceEvent, cancellationToken);
        else if (toDeviceEvent.Type == EventTypes.SecretSend) await ReceiveSecretAsync(toDeviceEvent, cancellationToken);
        else if (toDeviceEvent.Type.StartsWith("m.key.verification.", StringComparison.Ordinal)) await HandleVerificationEventAsync(toDeviceEvent.Type, toDeviceEvent.Sender, toDeviceEvent.Content, cancellationToken);
    }

    /// <summary>Upload device keys on first use, and one-time or fallback keys when the server is running low.</summary>
    /// <param name="serverCount">Unclaimed one-time keys on the server; null when unknown, which uploads only on first use.</param>
    /// <param name="fallbackUsed">Whether the published fallback key has been used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task UploadKeysIfNeededAsync(int? serverCount, bool fallbackUsed, CancellationToken cancellationToken)
    {
        // Decide what to upload; keep half the capacity on the server, leaving room for keys in flight
        int target = account.MaxOneTimeKeys / 2;
        int missing = !deviceKeysUploaded ? target : serverCount is int count ? Math.Max(0, target - count) : 0;
        bool newFallback = !deviceKeysUploaded || fallbackUsed;
        if (deviceKeysUploaded && missing == 0 && !newFallback) return;

        // Generate and sign
        if (missing > 0) account.GenerateOneTimeKeys(missing);
        if (newFallback) account.GenerateFallbackKey();
        JsonObject? deviceKeys = deviceKeysUploaded ? null : CreateDeviceKeys();
        JsonObject oneTimeKeys = SignKeys(account.OneTimeKeys, fallback: false);
        JsonObject fallbackKeys = SignKeys(account.FallbackKey, fallback: true);

        // Upload, then mark published
        await transport.UploadKeysAsync(deviceKeys, oneTimeKeys.Count > 0 ? oneTimeKeys : null, fallbackKeys.Count > 0 ? fallbackKeys : null, cancellationToken);
        account.MarkKeysAsPublished();
        deviceKeysUploaded = true;
        await SaveAccountAsync(cancellationToken);
    }

    /// <summary>Upload keys now when they have never been uploaded, e.g. right after login.</summary>
    public async Task UploadInitialKeysAsync(CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            await UploadKeysIfNeededAsync(null, fallbackUsed: false, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Build this device's signed <c>device_keys</c>.</summary>
    private JsonObject CreateDeviceKeys()
    {
        // Keys and supported algorithms
        IdentityKeys keys = account.IdentityKeys;
        JsonObject deviceKeys = new()
        {
            ["user_id"] = UserId,
            ["device_id"] = DeviceId,
            ["algorithms"] = new JsonArray(EncryptionAlgorithms.Olm, EncryptionAlgorithms.Megolm),
            ["keys"] = new JsonObject { [$"curve25519:{DeviceId}"] = keys.Curve25519, [$"ed25519:{DeviceId}"] = keys.Ed25519 },
        };
        // Return signed
        return Sign(deviceKeys);
    }

    /// <summary>Wrap public keys as signed key objects by <c>signed_curve25519:id</c>.</summary>
    private JsonObject SignKeys(IReadOnlyDictionary<string, string> keys, bool fallback)
    {
        // Sign each key object
        JsonObject result = new();
        foreach ((string keyId, string key) in keys)
        {
            JsonObject keyObject = new() { ["key"] = key };
            if (fallback) keyObject["fallback"] = true;
            result[$"{EncryptionAlgorithms.SignedCurve25519}:{keyId}"] = Sign(keyObject);
        }
        // Return keys
        return result;
    }

    /// <summary>Add this device's signature to <paramref name="value"/> and return it.</summary>
    private JsonObject Sign(JsonObject value)
    {
        // Sign the canonical form
        CanonicalJson.AddSignature(value, UserId, $"ed25519:{DeviceId}", account.Sign(CanonicalJson.SigningForm(value)));
        // Return signed object
        return value;
    }

    /// <summary>Return known devices of <paramref name="userId"/>; call <see cref="TrackUsersAsync"/> first to fetch them.</summary>
    public IReadOnlyList<DeviceInfo> GetDevices(string userId) => deviceLists.TryGetValue(userId, out DeviceListRecord? list) ? list.Devices : [];

    /// <summary>Fetch device lists of <paramref name="userIds"/> that are unknown or outdated.</summary>
    public async Task TrackUsersAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            await UpdateDeviceListsAsync(userIds, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Query unknown or outdated device lists and store the verified devices.</summary>
    private async Task UpdateDeviceListsAsync(IEnumerable<string> userIds, CancellationToken cancellationToken)
    {
        // Assert something to fetch
        List<string> stale = [.. userIds.Distinct().Where(u => !deviceLists.TryGetValue(u, out DeviceListRecord? list) || list.Outdated)];
        if (stale.Count == 0) return;

        // Query; own keys first, as our user-signing key vouches for others
        KeysQueryResponse response = await transport.QueryKeysAsync(stale, cancellationToken);
        foreach (string userId in stale.OrderBy(u => u == UserId ? 0 : 1))
        {
            // Cross-signing keys, then devices with their self-signing signatures
            UserIdentity? identity = VerifyIdentity(userId, response);
            IReadOnlyDictionary<string, JsonObject> devices = response.DeviceKeys.GetValueOrDefault(userId) ?? new Dictionary<string, JsonObject>();
            List<DeviceInfo> verified = [.. devices.Select(d => VerifyDeviceKeys(userId, d.Key, d.Value, identity?.SelfSigningKey)).OfType<DeviceInfo>()];
            DeviceListRecord record = new() { UserId = userId, Devices = verified, Outdated = response.Failures.Count > 0 && verified.Count == 0, Identity = identity };
            deviceLists[userId] = record;
            await store.SaveDeviceListAsync(record, cancellationToken);
        }
    }

    /// <summary>Return the user's cross-signing keys from <paramref name="response"/> when the master key is well-formed, with sub-keys only when the master signed them.</summary>
    private UserIdentity? VerifyIdentity(string userId, KeysQueryResponse response)
    {
        // Master key
        if (ParseCrossSigningKey(userId, response.MasterKeys.GetValueOrDefault(userId), CrossSigningUsages.Master) is not string master) return null;

        // Sub-keys signed by the master
        string? selfSigning = ParseCrossSigningKey(userId, response.SelfSigningKeys.GetValueOrDefault(userId), CrossSigningUsages.SelfSigning, master);
        string? userSigning = userId == UserId ? ParseCrossSigningKey(userId, response.UserSigningKeys.GetValueOrDefault(userId), CrossSigningUsages.UserSigning, master) : null;

        // Others' master signed by our user-signing key
        string? ourUserSigning = userId == UserId ? null : GetIdentity(UserId)?.UserSigningKey;
        bool signedByUs = ourUserSigning is not null && HasValidSignature(response.MasterKeys[userId], UserId, ourUserSigning);

        // Local trust survives only while the master key stays the same
        UserIdentity? known = GetIdentity(userId);
        // Return identity
        return new UserIdentity { UserId = userId, MasterKey = master, SelfSigningKey = selfSigning, UserSigningKey = userSigning, SignedByUserSigningKey = signedByUs ? ourUserSigning : null, Trusted = known?.MasterKey == master && known.Trusted };
    }

    /// <summary>Return the public key of a cross-signing key object of <paramref name="usage"/>, or null when malformed or, with <paramref name="signer"/>, not signed by it.</summary>
    private static string? ParseCrossSigningKey(string userId, JsonObject? json, string usage, string? signer = null)
    {
        // Shape
        CrossSigningKey? key = json?.Deserialize<CrossSigningKey>(MatrixJson.Options);
        if (key is null || key.UserId != userId || !key.Usage.Contains(usage) || key.PublicKey is not string publicKey || !key.Keys.ContainsKey($"ed25519:{publicKey}")) return null;
        // Return key, signed when required
        return signer is null || HasValidSignature(json!, userId, signer) ? publicKey : null;
    }

    /// <summary>Return whether <paramref name="json"/> carries a valid signature by <paramref name="userId"/>'s Ed25519 key <paramref name="publicKey"/>.</summary>
    private static bool HasValidSignature(JsonObject json, string userId, string publicKey)
    {
        // Signature under the key's own ID
        string? signature = CanonicalJson.GetSignature(json, userId, $"ed25519:{publicKey}");
        // Return verification
        return signature is not null && Ed25519.Verify(publicKey, CanonicalJson.SigningForm(json), signature);
    }

    /// <summary>Return the known cross-signing keys of <paramref name="userId"/>.</summary>
    public UserIdentity? GetIdentity(string userId) => deviceLists.GetValueOrDefault(userId)?.Identity;

    /// <summary>Return the device when its keys are self-signed and consistent, otherwise null; a known device keeps its first signing key.</summary>
    /// <param name="userId">Owner.</param>
    /// <param name="deviceId">Device ID the keys are listed under.</param>
    /// <param name="json">Signed device keys.</param>
    /// <param name="selfSigningKey">Owner's self-signing key, to note whether it signed the device.</param>
    private DeviceInfo? VerifyDeviceKeys(string userId, string deviceId, JsonObject json, string? selfSigningKey)
    {
        // Parse and check identity fields
        DeviceKeys? keys = json.Deserialize<DeviceKeys>(MatrixJson.Options);
        if (keys is null || keys.UserId != userId || keys.DeviceId != deviceId || keys.Curve25519Key is not string curve || keys.Ed25519Key is not string ed) return null;

        // Self-signature over the canonical form
        string? signature = CanonicalJson.GetSignature(json, userId, $"ed25519:{deviceId}");
        if (signature is null || !Ed25519.Verify(ed, CanonicalJson.SigningForm(json), signature)) return null;

        // A device ID must not change its fingerprint
        DeviceInfo? known = GetDevices(userId).FirstOrDefault(d => d.DeviceId == deviceId);
        if (known is not null && known.Ed25519Key != ed) return known;
        // Return verified device, noting cross-signing and keeping direct verification
        string? signedBy = selfSigningKey is not null && HasValidSignature(json, userId, selfSigningKey) ? selfSigningKey : null;
        return new DeviceInfo { UserId = userId, DeviceId = deviceId, Curve25519Key = curve, Ed25519Key = ed, DisplayName = keys.Unsigned?["device_display_name"]?.GetValue<string>(), SignedBySelfSigningKey = signedBy, Verified = known?.Verified == true };
    }

    /// <summary>Mark a known user's device list outdated.</summary>
    private async Task MarkOutdatedAsync(string userId, CancellationToken cancellationToken)
    {
        // Only tracked users
        if (!deviceLists.TryGetValue(userId, out DeviceListRecord? list) || list.Outdated) return;
        // Mark and save
        deviceLists[userId] = list with { Outdated = true };
        await store.SaveDeviceListAsync(deviceLists[userId], cancellationToken);
    }

    /// <summary>Save the account pickle.</summary>
    private Task SaveAccountAsync(CancellationToken cancellationToken) => store.SaveAccountAsync(new AccountRecord { Pickle = account.Pickle(pickleKey), DeviceKeysUploaded = deviceKeysUploaded }, cancellationToken);

    /// <summary>Wipe key material in memory.</summary>
    public void Dispose()
    {
        // Clear the account and free the lock
        account.Dispose();
        gate.Dispose();
    }
}
