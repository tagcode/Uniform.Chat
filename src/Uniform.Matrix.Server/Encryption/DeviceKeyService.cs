namespace Uniform.Matrix.Server.Encryption;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>The server's part of <see href="https://spec.matrix.org/latest/client-server-api/#end-to-end-encryption">end-to-end encryption</see>: device keys, one-time and fallback keys, cross-signing keys and signatures.</summary>
/// <remarks>The server never sees private keys. It checks that uploaded keys are signed as the spec requires, hands out one-time keys once, and records each change of a user's keys so other users' syncs list them in <c>device_lists.changed</c>.</remarks>
public sealed class DeviceKeyService
{
    /// <summary>Usage of the master cross-signing key.</summary>
    public const string Master = "master";
    /// <summary>Usage of the key that signs the user's own devices.</summary>
    public const string SelfSigning = "self_signing";
    /// <summary>Usage of the key that signs other users' master keys.</summary>
    public const string UserSigning = "user_signing";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;

    /// <summary>Create the service.</summary>
    public DeviceKeyService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
    }

    /// <summary>Store <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysupload">uploaded keys</see> of the requester's device; return the counts of its unclaimed one-time keys by algorithm.</summary>
    public async Task<JsonObject> UploadAsync(Requester requester, JsonObject? deviceKeys, JsonObject? oneTimeKeys, JsonObject? fallbackKeys, CancellationToken cancellationToken)
    {
        // Device keys of this device, signed by itself
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        bool changed = false;
        if (deviceKeys is not null)
        {
            if (deviceKeys["user_id"]?.GetValue<string>() != requester.UserId || deviceKeys["device_id"]?.GetValue<string>() != requester.DeviceId) throw Invalid("device_keys must be for this user and device.");
            string keyId = $"ed25519:{requester.DeviceId}";
            if (deviceKeys["keys"]?[keyId]?.GetValue<string>() is not string publicKey || !IsSignedBy(deviceKeys, requester.UserId, keyId, publicKey)) throw new MatrixException(HttpStatusCode.BadRequest, "M_INVALID_SIGNATURE", "device_keys must be signed by the device's Ed25519 key.");
            DeviceKeysRow? row = await db.DeviceKeys.SingleOrDefaultAsync(k => k.UserId == requester.UserId && k.DeviceId == requester.DeviceId, cancellationToken);
            string json = deviceKeys.ToJsonString();
            if (row?.Json != json)
            {
                if (row is null) db.DeviceKeys.Add(new DeviceKeysRow { UserId = requester.UserId, DeviceId = requester.DeviceId, Json = json });
                else row.Json = json;
                changed = true;
            }
        }

        // One-time keys are added, never replaced
        foreach ((string keyId, JsonNode? key) in oneTimeKeys ?? [])
        {
            string algorithm = Algorithm(keyId);
            OneTimeKeyRow? existing = await db.OneTimeKeys.AsNoTracking().SingleOrDefaultAsync(k => k.UserId == requester.UserId && k.DeviceId == requester.DeviceId && k.KeyId == keyId, cancellationToken);
            if (existing is not null && existing.Json != key?.ToJsonString()) throw Invalid($"One-time key {keyId} already exists with other content.");
            if (existing is null) db.OneTimeKeys.Add(new OneTimeKeyRow { UserId = requester.UserId, DeviceId = requester.DeviceId, KeyId = keyId, Algorithm = algorithm, Json = key?.ToJsonString() ?? "null" });
        }

        // Fallback keys replace the previous one of their algorithm
        foreach ((string keyId, JsonNode? key) in fallbackKeys ?? [])
        {
            string algorithm = Algorithm(keyId);
            FallbackKeyRow? existing = await db.FallbackKeys.SingleOrDefaultAsync(k => k.UserId == requester.UserId && k.DeviceId == requester.DeviceId && k.Algorithm == algorithm, cancellationToken);
            if (existing is null) db.FallbackKeys.Add(existing = new FallbackKeyRow { UserId = requester.UserId, DeviceId = requester.DeviceId, Algorithm = algorithm, KeyId = keyId, Json = "" });
            if (existing.KeyId != keyId || existing.Json != key?.ToJsonString()) existing.Used = false;
            existing.KeyId = keyId;
            existing.Json = key?.ToJsonString() ?? "null";
        }
        await db.SaveChangesAsync(cancellationToken);

        // Others learn of new device keys
        if (changed) await MarkChangedAsync([requester.UserId], cancellationToken);
        // Return counts
        return await CountOneTimeKeysAsync(db, requester.UserId, requester.DeviceId, cancellationToken);
    }

    /// <summary>Return the device and cross-signing keys of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysquery">queried users</see>: all devices, or the listed ones.</summary>
    /// <remarks>Signatures uploaded later are added from their owners and from the requester only; user-signing keys are returned only for the requester.</remarks>
    public async Task<JsonObject> QueryAsync(string requesterId, JsonObject query, CancellationToken cancellationToken)
    {
        // Each user's devices and keys
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        JsonObject deviceKeys = [], masterKeys = [], selfSigningKeys = [], userSigningKeys = [];
        foreach ((string userId, JsonNode? devicesNode) in query)
        {
            HashSet<string> wanted = [.. (devicesNode as JsonArray ?? []).Select(d => d!.GetValue<string>())];
            List<DeviceKeysRow> rows = await db.DeviceKeys.AsNoTracking().Where(k => k.UserId == userId).ToListAsync(cancellationToken);
            Dictionary<string, string?> names = await db.Devices.AsNoTracking().Where(d => d.UserId == userId).ToDictionaryAsync(d => d.DeviceId, d => d.DisplayName, cancellationToken);
            List<KeySignatureRow> signatures = await db.KeySignatures.AsNoTracking().Where(s => s.TargetUserId == userId && (s.SignerUserId == userId || s.SignerUserId == requesterId)).ToListAsync(cancellationToken);

            // Devices with their later signatures and display names
            JsonObject devices = [];
            foreach (DeviceKeysRow row in rows)
            {
                if (wanted.Count > 0 && !wanted.Contains(row.DeviceId)) continue;
                if (!names.TryGetValue(row.DeviceId, out string? name)) continue;
                JsonObject keys = WithSignatures(row.Json, signatures.Where(s => s.TargetKeyId == row.DeviceId));
                JsonObject unsigned = keys["unsigned"] as JsonObject ?? [];
                if (name is not null) unsigned["device_display_name"] = name;
                keys["unsigned"] = unsigned;
                devices[row.DeviceId] = keys;
            }
            deviceKeys[userId] = devices;

            // Cross-signing keys; the user-signing key is private to its owner
            foreach (CrossSigningKeyRow key in await db.CrossSigningKeys.AsNoTracking().Where(k => k.UserId == userId).ToListAsync(cancellationToken))
            {
                JsonObject target = key.Usage switch { Master => masterKeys, SelfSigning => selfSigningKeys, _ => userSigningKeys };
                if (key.Usage == UserSigning && userId != requesterId) continue;
                target[userId] = WithSignatures(key.Json, key.Usage == Master ? signatures.Where(s => s.TargetKeyId == key.PublicKey) : []);
            }
        }
        // Return keys
        return new JsonObject { ["device_keys"] = deviceKeys, ["master_keys"] = masterKeys, ["self_signing_keys"] = selfSigningKeys, ["user_signing_keys"] = userSigningKeys, ["failures"] = new JsonObject() };
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysclaim">Claims</see> one key per requested device: a one-time key, which is then removed, else the fallback key.</summary>
    public async Task<JsonObject> ClaimAsync(JsonObject request, CancellationToken cancellationToken)
    {
        // Each user, device and algorithm
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        JsonObject claimed = [];
        foreach ((string userId, JsonNode? devicesNode) in request)
        {
            JsonObject userKeys = [];
            foreach ((string deviceId, JsonNode? algorithmNode) in devicesNode as JsonObject ?? [])
            {
                string algorithm = algorithmNode?.GetValue<string>() ?? throw Invalid("Each device needs an algorithm.");
                if (await ClaimOneAsync(db, userId, deviceId, algorithm, cancellationToken) is not (string keyId, string json)) continue;
                userKeys[deviceId] = new JsonObject { [keyId] = JsonNode.Parse(json) };
            }
            if (userKeys.Count > 0) claimed[userId] = userKeys;
        }
        // Return keys
        return new JsonObject { ["one_time_keys"] = claimed, ["failures"] = new JsonObject() };
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keysdevice_signingupload">Stores cross-signing keys</see>; replacing an existing master key needs <paramref name="authorize"/>, which asks for the password.</summary>
    public async Task UploadCrossSigningKeysAsync(string userId, JsonObject? master, JsonObject? selfSigning, JsonObject? userSigning, Func<Task> authorize, CancellationToken cancellationToken)
    {
        // Current keys
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        Dictionary<string, CrossSigningKeyRow> current = await db.CrossSigningKeys.Where(k => k.UserId == userId).ToDictionaryAsync(k => k.Usage, cancellationToken);

        // Replacing keys needs the password; the first upload does not
        bool replaces = new[] { (Master, master), (SelfSigning, selfSigning), (UserSigning, userSigning) }.Any(k => k.Item2 is not null && current.TryGetValue(k.Item1, out CrossSigningKeyRow? row) && row.Json != k.Item2.ToJsonString());
        if (replaces) await authorize();

        // Master key, then the keys it signs
        string masterPublic = master is null ? current.GetValueOrDefault(Master)?.PublicKey ?? throw Invalid("Upload a master key first.") : PublicKey(master, userId, Master);
        List<(string Usage, JsonObject Key)> keys = [];
        if (master is not null) keys.Add((Master, master));
        foreach ((string usage, JsonObject? key) in new[] { (SelfSigning, selfSigning), (UserSigning, userSigning) })
        {
            if (key is null) continue;
            if (!IsSignedBy(key, userId, $"ed25519:{masterPublic}", masterPublic)) throw new MatrixException(HttpStatusCode.BadRequest, "M_INVALID_SIGNATURE", $"The {usage} key must be signed by the master key.");
            keys.Add((usage, key));
        }

        // Store; a new master key drops the old signatures of it
        foreach ((string usage, JsonObject key) in keys)
        {
            string publicKey = PublicKey(key, userId, usage);
            if (current.TryGetValue(usage, out CrossSigningKeyRow? row)) db.CrossSigningKeys.Remove(row);
            db.CrossSigningKeys.Add(new CrossSigningKeyRow { UserId = userId, Usage = usage, PublicKey = publicKey, Json = key.ToJsonString() });
        }
        if (master is not null && current.GetValueOrDefault(Master)?.PublicKey is string oldMaster && oldMaster != masterPublic) await db.KeySignatures.Where(s => s.TargetUserId == userId && s.TargetKeyId == oldMaster).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await MarkChangedAsync([userId], cancellationToken);
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3keyssignaturesupload">Stores signatures</see> of devices and master keys after checking them; returns the failures by user and key.</summary>
    /// <remarks>
    /// Accepted: the requester's devices signed by their self-signing key, their master key signed by one of their devices,
    /// and other users' master keys signed by the requester's user-signing key. The uploaded object must be the stored key.
    /// </remarks>
    public async Task<JsonObject> UploadSignaturesAsync(string requesterId, JsonObject body, CancellationToken cancellationToken)
    {
        // Each signed object
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        JsonObject failures = [];
        HashSet<string> changedUsers = [];
        foreach ((string targetUser, JsonNode? keysNode) in body)
        {
            foreach ((string targetKey, JsonNode? signedNode) in keysNode as JsonObject ?? [])
            {
                string? error = signedNode is JsonObject signed ? await AddSignaturesAsync(db, requesterId, targetUser, targetKey, signed, cancellationToken) : "Not an object.";
                if (error is null)
                {
                    changedUsers.Add(targetUser == requesterId ? targetUser : requesterId);
                    continue;
                }
                JsonObject userFailures = failures[targetUser] as JsonObject ?? (JsonObject)(failures[targetUser] = new JsonObject());
                userFailures[targetKey] = new JsonObject { ["errcode"] = "M_INVALID_SIGNATURE", ["error"] = error };
            }
        }

        // Others learn of the new signatures
        await db.SaveChangesAsync(cancellationToken);
        if (changedUsers.Count > 0) await MarkChangedAsync(changedUsers, cancellationToken);
        // Return failures
        return new JsonObject { ["failures"] = failures };
    }

    /// <summary>Remove the keys and signatures of deleted devices, and tell others.</summary>
    public async Task DeleteDevicesAsync(string userId, IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken)
    {
        // Keys of the devices
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        int removed = await db.DeviceKeys.Where(k => k.UserId == userId && deviceIds.Contains(k.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await db.OneTimeKeys.Where(k => k.UserId == userId && deviceIds.Contains(k.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await db.FallbackKeys.Where(k => k.UserId == userId && deviceIds.Contains(k.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await db.KeySignatures.Where(s => s.TargetUserId == userId && deviceIds.Contains(s.TargetKeyId)).ExecuteDeleteAsync(cancellationToken);
        await db.ToDevice.Where(m => m.UserId == userId && deviceIds.Contains(m.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        // Changed for others when keys went away
        if (removed > 0) await MarkChangedAsync([userId], cancellationToken);
    }

    /// <summary>Return the counts of a device's unclaimed one-time keys by algorithm.</summary>
    public static async Task<JsonObject> CountOneTimeKeysAsync(ServerDbContext dbContext, string userId, string deviceId, CancellationToken cancellationToken)
    {
        // Grouped by algorithm
        Dictionary<string, int> counts = await dbContext.OneTimeKeys.AsNoTracking().Where(k => k.UserId == userId && k.DeviceId == deviceId).GroupBy(k => k.Algorithm).ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
        JsonObject result = [];
        foreach ((string algorithm, int count) in counts) result[algorithm] = count;
        // Return counts, with signed_curve25519 always present as clients expect
        result[EncryptionAlgorithms.SignedCurve25519] ??= 0;
        return new JsonObject { ["one_time_key_counts"] = result };
    }

    /// <summary>Return the algorithms of a device's fallback keys that were not claimed yet.</summary>
    public static async Task<JsonArray> UnusedFallbackKeyTypesAsync(ServerDbContext dbContext, string userId, string deviceId, CancellationToken cancellationToken)
    {
        // Unused fallback keys
        List<string> types = await dbContext.FallbackKeys.AsNoTracking().Where(k => k.UserId == userId && k.DeviceId == deviceId && !k.Used).Select(k => k.Algorithm).ToListAsync(cancellationToken);
        // Return algorithms
        return new JsonArray([.. types.Select(t => (JsonNode)t)]);
    }

    /// <summary>Record a change of the users' keys at a new stream position.</summary>
    public Task MarkChangedAsync(IEnumerable<string> userIds, CancellationToken cancellationToken) =>
        stream.WriteAsync(async position =>
        {
            // Latest change per user
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            foreach (string userId in userIds.Distinct())
            {
                DeviceListChangeRow? row = await db.DeviceListChanges.SingleOrDefaultAsync(c => c.UserId == userId, cancellationToken);
                if (row is null) db.DeviceListChanges.Add(new DeviceListChangeRow { UserId = userId, Stream = position });
                else row.Stream = position;
            }
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Check and store the requester's signatures of one key; return an error, or null when stored.</summary>
    private static async Task<string?> AddSignaturesAsync(ServerDbContext dbContext, string requesterId, string targetUser, string targetKey, JsonObject signed, CancellationToken cancellationToken)
    {
        // The stored key it signs: a device of the requester, or a master key
        string? storedJson;
        bool isDevice = false;
        if (targetUser == requesterId && await dbContext.DeviceKeys.AsNoTracking().SingleOrDefaultAsync(k => k.UserId == targetUser && k.DeviceId == targetKey, cancellationToken) is DeviceKeysRow device)
        {
            storedJson = device.Json;
            isDevice = true;
        }
        else
        {
            storedJson = (await dbContext.CrossSigningKeys.AsNoTracking().SingleOrDefaultAsync(k => k.UserId == targetUser && k.Usage == Master && k.PublicKey == targetKey, cancellationToken))?.Json;
        }
        if (storedJson is null) return "Unknown key.";
        if (CanonicalJson.SigningForm(JsonNode.Parse(storedJson)!.AsObject()) != CanonicalJson.SigningForm(signed)) return "The signed object is not the stored key.";

        // Signing keys allowed for this target
        Dictionary<string, string> signers = [];
        Dictionary<string, CrossSigningKeyRow> own = await dbContext.CrossSigningKeys.AsNoTracking().Where(k => k.UserId == requesterId).ToDictionaryAsync(k => k.Usage, cancellationToken);
        if (isDevice && own.GetValueOrDefault(SelfSigning) is CrossSigningKeyRow selfKey) signers[$"ed25519:{selfKey.PublicKey}"] = selfKey.PublicKey;
        if (!isDevice && targetUser != requesterId && own.GetValueOrDefault(UserSigning) is CrossSigningKeyRow userKey) signers[$"ed25519:{userKey.PublicKey}"] = userKey.PublicKey;
        if (!isDevice && targetUser == requesterId)
        {
            foreach (DeviceKeysRow ownDevice in await dbContext.DeviceKeys.AsNoTracking().Where(k => k.UserId == requesterId).ToListAsync(cancellationToken))
            {
                if (JsonNode.Parse(ownDevice.Json)?["keys"]?[$"ed25519:{ownDevice.DeviceId}"]?.GetValue<string>() is string devicePublic) signers[$"ed25519:{ownDevice.DeviceId}"] = devicePublic;
            }
        }

        // Every new signature by the requester must verify
        int added = 0;
        foreach ((string keyId, JsonNode? signatureNode) in signed["signatures"]?[requesterId] as JsonObject ?? [])
        {
            if (!signers.TryGetValue(keyId, out string? publicKey)) continue;
            string signature = signatureNode?.GetValue<string>() ?? "";
            if (!Ed25519.Verify(publicKey, CanonicalJson.SigningForm(signed), signature)) return $"Signature by {keyId} does not verify.";
            KeySignatureRow? row = await dbContext.KeySignatures.SingleOrDefaultAsync(s => s.TargetUserId == targetUser && s.TargetKeyId == targetKey && s.SignerUserId == requesterId && s.SignerKeyId == keyId, cancellationToken);
            if (row is null) dbContext.KeySignatures.Add(new KeySignatureRow { TargetUserId = targetUser, TargetKeyId = targetKey, SignerUserId = requesterId, SignerKeyId = keyId, Signature = signature });
            else row.Signature = signature;
            added++;
        }
        // Return verdict
        return added > 0 ? null : "No signature by a key allowed to sign it.";
    }

    /// <summary>Take one one-time key of a device, or its fallback key; null when it has neither.</summary>
    private static async Task<(string KeyId, string Json)?> ClaimOneAsync(ServerDbContext dbContext, string userId, string deviceId, string algorithm, CancellationToken cancellationToken)
    {
        // One-time keys go once: retry when another claim took the same key
        while (await dbContext.OneTimeKeys.AsNoTracking().Where(k => k.UserId == userId && k.DeviceId == deviceId && k.Algorithm == algorithm).OrderBy(k => k.KeyId).FirstOrDefaultAsync(cancellationToken) is OneTimeKeyRow key)
        {
            int taken = await dbContext.OneTimeKeys.Where(k => k.UserId == userId && k.DeviceId == deviceId && k.KeyId == key.KeyId).ExecuteDeleteAsync(cancellationToken);
            if (taken == 1) return (key.KeyId, key.Json);
        }

        // Fallback key, marked used so the device makes a new one
        FallbackKeyRow? fallback = await dbContext.FallbackKeys.SingleOrDefaultAsync(k => k.UserId == userId && k.DeviceId == deviceId && k.Algorithm == algorithm, cancellationToken);
        if (fallback is null) return null;
        fallback.Used = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        // Return fallback
        return (fallback.KeyId, fallback.Json);
    }

    /// <summary>Return a key's JSON with signatures uploaded later merged in.</summary>
    private static JsonObject WithSignatures(string json, IEnumerable<KeySignatureRow> signatures)
    {
        // Merge by signer and key
        JsonObject key = JsonNode.Parse(json)!.AsObject();
        JsonObject all = key["signatures"] as JsonObject ?? (JsonObject)(key["signatures"] = new JsonObject());
        foreach (KeySignatureRow signature in signatures)
        {
            JsonObject bySigner = all[signature.SignerUserId] as JsonObject ?? (JsonObject)(all[signature.SignerUserId] = new JsonObject());
            bySigner[signature.SignerKeyId] = signature.Signature;
        }
        // Return key
        return key;
    }

    /// <summary>Return the public key of a cross-signing key after checking its owner, usage and single Ed25519 key.</summary>
    private static string PublicKey(JsonObject key, string userId, string usage)
    {
        // Owner and usage
        if (key["user_id"]?.GetValue<string>() != userId) throw Invalid($"The {usage} key must be for this user.");
        if (key["usage"] is not JsonArray usages || !usages.Any(u => u?.GetValue<string>() == usage)) throw Invalid($"The {usage} key must have usage {usage}.");

        // Exactly one Ed25519 key, named by itself
        List<KeyValuePair<string, JsonNode?>> keys = [.. key["keys"] as JsonObject ?? []];
        if (keys.Count != 1 || !keys[0].Key.StartsWith("ed25519:", StringComparison.Ordinal) || keys[0].Value?.GetValueKind() != JsonValueKind.String) throw Invalid($"The {usage} key must have one Ed25519 key.");
        string publicKey = keys[0].Value!.GetValue<string>();
        if (keys[0].Key != $"ed25519:{publicKey}") throw Invalid($"The {usage} key ID must be its public key.");
        // Return public key
        return publicKey;
    }

    /// <summary>Return whether <paramref name="value"/> carries a valid signature by <paramref name="userId"/>'s key <paramref name="keyId"/>.</summary>
    private static bool IsSignedBy(JsonObject value, string userId, string keyId, string publicKey) =>
        CanonicalJson.GetSignature(value, userId, keyId) is string signature && Ed25519.Verify(publicKey, CanonicalJson.SigningForm(value), signature);

    /// <summary>Return the algorithm of a key ID like <c>signed_curve25519:AAAAHg</c>.</summary>
    private static string Algorithm(string keyId) => keyId.Contains(':') ? keyId[..keyId.IndexOf(':')] : throw Invalid($"Key ID {keyId} has no algorithm.");

    /// <summary>Return an <c>M_INVALID_PARAM</c> error.</summary>
    private static MatrixException Invalid(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, message);
}
