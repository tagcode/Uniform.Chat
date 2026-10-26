namespace Uniform.Matrix.Protocol.Crypto;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>How much a device is trusted, see <see cref="OlmMachine.GetDeviceTrust"/>.</summary>
public enum DeviceTrust
{
    /// <summary>Neither verified nor signed by a trusted identity.</summary>
    Unverified,
    /// <summary>Signed by its owner's self-signing key, and the owner's identity is trusted.</summary>
    CrossSigned,
    /// <summary>Verified directly, e.g. by comparing emoji.</summary>
    Verified,
    /// <summary>The device running this machine.</summary>
    ThisDevice,
}

/// <summary>Cross-signing, key backup and secret storage state of the account, from <see cref="OlmMachine.GetSecurityStatusAsync"/>.</summary>
public sealed record SecurityStatus
{
    /// <summary>Whether the account has published cross-signing keys.</summary>
    public bool CrossSigningSetUp { get; init; }
    /// <summary>Whether this device trusts the account's cross-signing identity.</summary>
    public bool IdentityTrusted { get; init; }
    /// <summary>Whether this device holds all three private cross-signing keys, matching the published ones.</summary>
    public bool HasPrivateKeys { get; init; }
    /// <summary>Whether this device is signed by the account's self-signing key.</summary>
    public bool ThisDeviceCrossSigned { get; init; }
    /// <summary>Current key backup version on the server, when any.</summary>
    public string? BackupVersion { get; init; }
    /// <summary>Whether this device holds the private key of the current backup, so it uploads and restores keys.</summary>
    public bool BackupUsable { get; init; }
    /// <summary>Whether the account has a default secret storage key.</summary>
    public bool SecretStorageSetUp { get; init; }
}

/// <summary>Outcome of <see cref="OlmMachine.RecoverAsync"/>.</summary>
public sealed record RecoveryResult
{
    /// <summary>Whether the cross-signing keys were restored and this device signed.</summary>
    public bool CrossSigningRestored { get; init; }
    /// <summary>Whether the backup key was restored.</summary>
    public bool BackupRestored { get; init; }
    /// <summary>Room keys imported from the backup.</summary>
    public int KeysImported { get; init; }
}

// Cross-signing keys, trust and recovery from secret storage.
public sealed partial class OlmMachine
{
    private const string SecretValuePrefix = "secret:";
    private static readonly string[] StoredSecretNames = [SecretNames.CrossSigningMaster, SecretNames.CrossSigningSelfSigning, SecretNames.CrossSigningUserSigning, SecretNames.MegolmBackup];

    /// <summary>Return how much <paramref name="device"/> is trusted.</summary>
    public DeviceTrust GetDeviceTrust(DeviceInfo device)
    {
        // Direct knowledge first
        if (device.UserId == UserId && device.DeviceId == DeviceId) return DeviceTrust.ThisDevice;
        if (device.Verified) return DeviceTrust.Verified;

        // Signed by a trusted identity's self-signing key
        UserIdentity? identity = GetIdentity(device.UserId);
        bool crossSigned = identity?.SelfSigningKey is string selfSigning && device.SignedBySelfSigningKey == selfSigning && IsIdentityTrusted(identity);
        // Return trust
        return crossSigned ? DeviceTrust.CrossSigned : DeviceTrust.Unverified;
    }

    /// <summary>Return whether a user's cross-signing identity is trusted: verified directly, our own with its private key here, or signed by our trusted user-signing key.</summary>
    public bool IsIdentityTrusted(UserIdentity identity)
    {
        // Verified directly
        if (identity.Trusted) return true;
        if (identity.UserId == UserId) return GetPrivateKey(SecretNames.CrossSigningMaster, identity.MasterKey) is not null;

        // Vouched for by our own trusted identity
        UserIdentity? own = GetIdentity(UserId);
        // Return trust
        return own?.UserSigningKey is string userSigning && identity.SignedByUserSigningKey == userSigning && IsIdentityTrusted(own);
    }

    /// <summary>Return whether <paramref name="device"/> is ours and trusted enough to share keys and secrets with.</summary>
    private bool IsTrustedOwnDevice(DeviceInfo device) => device.UserId == UserId && GetDeviceTrust(device) is DeviceTrust.CrossSigned or DeviceTrust.Verified;

    /// <summary>Report cross-signing, backup and secret storage state, querying the server for current keys.</summary>
    public async Task<SecurityStatus> GetSecurityStatusAsync(CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Current keys and backup
            await RefreshDeviceListAsync(UserId, cancellationToken);
            UserIdentity? identity = GetIdentity(UserId);
            KeyBackupVersion? backup = await transport.GetKeyBackupVersionAsync(cancellationToken);
            bool secretStorage = await new SecretStorage(transport).GetDefaultKeyAsync(cancellationToken) is not null;

            // Compare with what this device holds
            bool privateKeysMatch = identity is not null
                && GetPrivateKey(SecretNames.CrossSigningMaster, identity.MasterKey) is not null
                && GetPrivateKey(SecretNames.CrossSigningSelfSigning, identity.SelfSigningKey) is not null
                && GetPrivateKey(SecretNames.CrossSigningUserSigning, identity.UserSigningKey) is not null;
            DeviceInfo? self = GetDevices(UserId).FirstOrDefault(d => d.DeviceId == DeviceId);
            // Return status
            return new SecurityStatus
            {
                CrossSigningSetUp = identity is not null,
                IdentityTrusted = identity is not null && IsIdentityTrusted(identity),
                HasPrivateKeys = privateKeysMatch,
                ThisDeviceCrossSigned = self?.SignedBySelfSigningKey is string signer && signer == identity?.SelfSigningKey,
                BackupVersion = backup?.Version,
                BackupUsable = backup is not null && GetBackupSecretFor(backup) is not null,
                SecretStorageSetUp = secretStorage,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Set up cross-signing, a key backup and secret storage on an account without cross-signing; return the recovery key to show the user once.</summary>
    /// <param name="password">Password, when the server asks for it; when null, <c>UserInteractiveAuthException</c> may be thrown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Throws <see cref="InvalidOperationException"/> when the account already has cross-signing keys: replacing them would distrust every verified device, so use <see cref="RecoverAsync"/> instead.</remarks>
    public async Task<string> SetUpRecoveryAsync(string? password, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Never replace existing keys
            await RefreshDeviceListAsync(UserId, cancellationToken);
            if (GetIdentity(UserId) is not null) throw new InvalidOperationException("Cross-signing is already set up on this account. Recover with the recovery key instead.");

            // Keys: the master signs the other two, this device signs the master
            (string masterSecret, string masterKey) = Ed25519.GenerateKey();
            (string selfSigningSecret, string selfSigningKey) = Ed25519.GenerateKey();
            (string userSigningSecret, string userSigningKey) = Ed25519.GenerateKey();
            JsonObject master = Sign(CreateCrossSigningKey(masterKey, CrossSigningUsages.Master));
            JsonObject selfSigning = SignWith(CreateCrossSigningKey(selfSigningKey, CrossSigningUsages.SelfSigning), masterSecret, masterKey);
            JsonObject userSigning = SignWith(CreateCrossSigningKey(userSigningKey, CrossSigningUsages.UserSigning), masterSecret, masterKey);
            await transport.UploadCrossSigningKeysAsync(master, selfSigning, userSigning, password, cancellationToken);

            // Keep the private keys, trust the identity and sign this device
            await SavePrivateKeyAsync(SecretNames.CrossSigningMaster, masterSecret, cancellationToken);
            await SavePrivateKeyAsync(SecretNames.CrossSigningSelfSigning, selfSigningSecret, cancellationToken);
            await SavePrivateKeyAsync(SecretNames.CrossSigningUserSigning, userSigningSecret, cancellationToken);
            await RefreshDeviceListAsync(UserId, cancellationToken);
            await UpdateIdentityAsync(UserId, i => i with { Trusted = true }, cancellationToken);
            await SignOwnDevicesAsync([DeviceId], cancellationToken);

            // New key backup
            await CreateBackupAsync(cancellationToken);

            // Secret storage holding all private keys
            SecretStorage storage = new(transport);
            (string keyId, byte[] key) = await storage.CreateKeyAsync(cancellationToken: cancellationToken);
            foreach (string name in StoredSecretNames)
            {
                if (privateKeys.TryGetValue(name, out string? secret)) await storage.StoreSecretAsync(name, secret, keyId, key, cancellationToken);
            }
            // Return recovery key
            return SecretStorageCrypto.EncodeRecoveryKey(key);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Unlock secret storage with a recovery key or passphrase, restores cross-signing and the backup key, signs this device, and import backed-up room keys.</summary>
    /// <remarks>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for a wrong key, and <see cref="InvalidOperationException"/> when the account has no secret storage.</remarks>
    public async Task<RecoveryResult> RecoverAsync(string recoveryKeyOrPassphrase, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Unlock
            SecretStorage storage = new(transport);
            if (await storage.GetDefaultKeyAsync(cancellationToken) is not (string keyId, SecretStorageKeyDescription description)) throw new InvalidOperationException("This account has no secret storage. Set up recovery first.");
            byte[] key = SecretStorage.Unlock(description, recoveryKeyOrPassphrase);

            // Cross-signing keys that match the published ones
            await RefreshDeviceListAsync(UserId, cancellationToken);
            UserIdentity? identity = GetIdentity(UserId);
            int restored = 0;
            foreach ((string name, string? expected) in new[] { (SecretNames.CrossSigningMaster, identity?.MasterKey), (SecretNames.CrossSigningSelfSigning, identity?.SelfSigningKey), (SecretNames.CrossSigningUserSigning, identity?.UserSigningKey) })
            {
                if (expected is null || await storage.GetSecretAsync(name, keyId, key, cancellationToken) is not string secret) continue;
                if (TryGetPublicKey(secret) != expected) continue;
                await SavePrivateKeyAsync(name, secret, cancellationToken);
                restored++;
            }

            // Trust the identity and sign this device
            bool crossSigning = identity is not null && GetPrivateKey(SecretNames.CrossSigningMaster, identity.MasterKey) is not null;
            if (crossSigning) await UpdateIdentityAsync(UserId, i => i with { Trusted = true }, cancellationToken);
            if (GetPrivateKey(SecretNames.CrossSigningSelfSigning, identity?.SelfSigningKey) is not null) await SignOwnDevicesAsync([DeviceId], cancellationToken);

            // Backup key, then its room keys
            string? backupSecret = await storage.GetSecretAsync(SecretNames.MegolmBackup, keyId, key, cancellationToken);
            int? imported = backupSecret is null ? null : await UseBackupKeyAsync(backupSecret, cancellationToken);
            // Return outcome
            return new RecoveryResult { CrossSigningRestored = crossSigning, BackupRestored = imported is not null, KeysImported = imported ?? 0 };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Sign own devices' keys with the self-signing key and upload the signatures; devices without current keys are skipped.</summary>
    private async Task SignOwnDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken)
    {
        // Assert the self-signing key
        if (GetPrivateKey(SecretNames.CrossSigningSelfSigning, GetIdentity(UserId)?.SelfSigningKey) is not string secret) return;
        string publicKey = Ed25519.GetPublicKey(secret);

        // Sign the published device keys
        KeysQueryResponse response = await transport.QueryKeysAsync([UserId], cancellationToken);
        Dictionary<string, JsonObject> signed = [];
        foreach (string deviceId in deviceIds)
        {
            if (response.DeviceKeys.GetValueOrDefault(UserId)?.GetValueOrDefault(deviceId) is not JsonObject keys) continue;
            signed[deviceId] = SignWith(keys, secret, publicKey);
        }
        if (signed.Count == 0) return;

        // Upload, then refresh to see the signatures
        await transport.UploadSignaturesAsync(new Dictionary<string, Dictionary<string, JsonObject>> { [UserId] = signed }, cancellationToken);
        await RefreshDeviceListAsync(UserId, cancellationToken);
    }

    /// <summary>Sign another user's master key with our user-signing key and upload the signature, so our other devices trust them too.</summary>
    private async Task SignUserAsync(string userId, CancellationToken cancellationToken)
    {
        // Assert the user-signing key and their master key
        if (GetPrivateKey(SecretNames.CrossSigningUserSigning, GetIdentity(UserId)?.UserSigningKey) is not string secret) return;
        KeysQueryResponse response = await transport.QueryKeysAsync([userId], cancellationToken);
        if (response.MasterKeys.GetValueOrDefault(userId) is not JsonObject master || ParseCrossSigningKey(userId, master, CrossSigningUsages.Master) is not string masterKey) return;

        // Upload, then refresh
        JsonObject signed = SignWith(master, secret, Ed25519.GetPublicKey(secret));
        await transport.UploadSignaturesAsync(new Dictionary<string, Dictionary<string, JsonObject>> { [userId] = new() { [masterKey] = signed } }, cancellationToken);
        await RefreshDeviceListAsync(userId, cancellationToken);
    }

    /// <summary>Build an unsigned cross-signing key object.</summary>
    private JsonObject CreateCrossSigningKey(string publicKey, string usage) => new()
    {
        ["user_id"] = UserId,
        ["usage"] = new JsonArray(usage),
        ["keys"] = new JsonObject { [$"ed25519:{publicKey}"] = publicKey },
    };

    /// <summary>Return a copy of <paramref name="value"/> signed only by the Ed25519 key <paramref name="secret"/>, as signature uploads expect.</summary>
    private JsonObject SignWith(JsonObject value, string secret, string publicKey)
    {
        // Strip existing signatures and unsigned data, then sign
        JsonObject copy = (JsonObject)JsonNode.Parse(CanonicalJson.SigningForm(value))!;
        CanonicalJson.AddSignature(copy, UserId, $"ed25519:{publicKey}", Ed25519.Sign(secret, CanonicalJson.SigningForm(copy)));
        // Return signed copy
        return copy;
    }

    /// <summary>Return the private key <paramref name="name"/> when its public key is <paramref name="expectedPublicKey"/>; null otherwise, e.g. after keys were replaced elsewhere.</summary>
    private string? GetPrivateKey(string name, string? expectedPublicKey)
    {
        // Assert key and match
        if (expectedPublicKey is null || !privateKeys.TryGetValue(name, out string? secret)) return null;
        // Return matching key
        return TryGetPublicKey(secret) == expectedPublicKey ? secret : null;
    }

    /// <summary>Return the Ed25519 public key of a private key, or null when it is not one.</summary>
    private static string? TryGetPublicKey(string secret)
    {
        // Derive
        try
        {
            return Ed25519.GetPublicKey(NormalizeKey(secret));
        }
        catch (OlmException)
        {
            // Done; not a key
            return null;
        }
    }

    /// <summary>Return a base64 key in the unpadded form stored and sent; other clients may pad.</summary>
    private static string NormalizeKey(string key) => Base64.EncodeUnpadded(Base64.Decode(key));

    /// <summary>Keep a private key in memory and in the store, encrypted with the pickle key.</summary>
    private async Task SavePrivateKeyAsync(string name, string secret, CancellationToken cancellationToken)
    {
        // Encrypt like other stored secrets
        string normalized = NormalizeKey(secret);
        privateKeys[name] = normalized;
        await store.SaveValueAsync(SecretValuePrefix + name, JsonSerializer.Serialize(SecretStorageCrypto.Encrypt(pickleKey, name, normalized), MatrixJson.Options), cancellationToken);
    }

    /// <summary>Query a user's devices and cross-signing keys again.</summary>
    private async Task RefreshDeviceListAsync(string userId, CancellationToken cancellationToken)
    {
        // Mark outdated, then update
        if (deviceLists.TryGetValue(userId, out DeviceListRecord? list)) deviceLists[userId] = list with { Outdated = true };
        await UpdateDeviceListsAsync([userId], cancellationToken);
    }

    /// <summary>Change and store a user's identity, e.g. to trust it.</summary>
    private async Task UpdateIdentityAsync(string userId, Func<UserIdentity, UserIdentity> update, CancellationToken cancellationToken)
    {
        // Assert known identity
        if (!deviceLists.TryGetValue(userId, out DeviceListRecord? list) || list.Identity is null) return;
        // Store
        deviceLists[userId] = list with { Identity = update(list.Identity) };
        await store.SaveDeviceListAsync(deviceLists[userId], cancellationToken);
    }

    /// <summary>Mark a device verified directly and store it.</summary>
    private async Task MarkDeviceVerifiedAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        // Assert known device
        if (!deviceLists.TryGetValue(device.UserId, out DeviceListRecord? list)) return;
        // Store
        deviceLists[device.UserId] = list with { Devices = [.. list.Devices.Select(d => d.DeviceId == device.DeviceId && d.Ed25519Key == device.Ed25519Key ? d with { Verified = true } : d)] };
        await store.SaveDeviceListAsync(deviceLists[device.UserId], cancellationToken);
    }
}
