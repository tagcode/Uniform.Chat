namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Server-side key backup.
public sealed partial class OlmMachine
{
    private const string BackupVersionValue = "backup_version";
    private const string WrongBackupVersion = "M_WRONG_ROOM_KEYS_VERSION";
    private const int BackupBatchSize = 100;

    /// <summary>Upload room keys not yet in the backup; return how many. Does nothing without a backup whose private key this device holds.</summary>
    public async Task<int> BackUpRoomKeysAsync(CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Assert a usable backup
            string? version = await store.LoadValueAsync(BackupVersionValue, cancellationToken);
            if (version is null || !privateKeys.TryGetValue(SecretNames.MegolmBackup, out string? secret)) return 0;
            string publicKey = PkEncryption.GetPublicKey(secret);

            // Upload in batches
            int total = 0;
            while (true)
            {
                IReadOnlyList<InboundGroupSessionRecord> sessions = await store.LoadSessionsToBackUpAsync(BackupBatchSize, cancellationToken);
                if (sessions.Count == 0) break;
                try
                {
                    await transport.UploadRoomKeysAsync(version, CreateBackup(sessions, publicKey), cancellationToken);
                }
                catch (MatrixException ex) when (ex.Errcode is WrongBackupVersion or ErrorCodes.NotFound)
                {
                    // Backup replaced or deleted elsewhere; stop until recovered again
                    await store.SaveValueAsync(BackupVersionValue, null, cancellationToken);
                    break;
                }
                foreach (InboundGroupSessionRecord session in sessions) await store.SaveInboundGroupSessionAsync(session with { BackedUp = true }, cancellationToken);
                total += sessions.Count;
            }
            // Return count
            return total;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Download and import all keys of the current backup; return how many were new. Throws <see cref="InvalidOperationException"/> when this device lacks the backup key.</summary>
    public async Task<int> RestoreBackupAsync(CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Assert backup and key
            KeyBackupVersion? backup = await transport.GetKeyBackupVersionAsync(cancellationToken);
            if (backup?.Version is not string version || GetBackupSecretFor(backup) is not string secret) throw new InvalidOperationException("This device does not have the key of the current backup. Recover with the recovery key first.");
            // Return imported count
            return await ImportBackupAsync(version, secret, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Create a new backup version signed by this device and the master key, and keep its private key; all sessions get uploaded again.</summary>
    private async Task CreateBackupAsync(CancellationToken cancellationToken)
    {
        // New key pair; the public key goes in signed auth data
        string secret = Base64.EncodeUnpadded(RandomNumberGenerator.GetBytes(32));
        JsonObject authData = Sign(new JsonObject { ["public_key"] = PkEncryption.GetPublicKey(secret) });
        if (GetPrivateKey(SecretNames.CrossSigningMaster, GetIdentity(UserId)?.MasterKey) is string master)
        {
            string masterKey = Ed25519.GetPublicKey(master);
            CanonicalJson.AddSignature(authData, UserId, $"ed25519:{masterKey}", Ed25519.Sign(master, CanonicalJson.SigningForm(authData)));
        }

        // Create, keep the key, and back everything up again
        string version = await transport.CreateKeyBackupVersionAsync(KeyBackupAlgorithms.MegolmV1, authData, cancellationToken);
        await SavePrivateKeyAsync(SecretNames.MegolmBackup, secret, cancellationToken);
        await store.SaveValueAsync(BackupVersionValue, version, cancellationToken);
        await store.ResetBackedUpAsync(cancellationToken);
    }

    /// <summary>Keep <paramref name="secret"/> when it is the current backup's key, then import the backup; return the imported count, or null when the key does not match.</summary>
    private async Task<int?> UseBackupKeyAsync(string secret, CancellationToken cancellationToken)
    {
        // Assert the key matches the current backup
        KeyBackupVersion? backup = await transport.GetKeyBackupVersionAsync(cancellationToken);
        string normalized = NormalizeKey(secret);
        if (backup?.Version is not string version || backup.Algorithm != KeyBackupAlgorithms.MegolmV1 || TryGetBackupPublicKey(normalized) != backup.AuthData?["public_key"]?.GetValue<string>()) return null;

        // Keep key and version; a different version needs every session again
        await SavePrivateKeyAsync(SecretNames.MegolmBackup, normalized, cancellationToken);
        if (await store.LoadValueAsync(BackupVersionValue, cancellationToken) != version) await store.ResetBackedUpAsync(cancellationToken);
        await store.SaveValueAsync(BackupVersionValue, version, cancellationToken);
        // Return imported count
        return await ImportBackupAsync(version, normalized, cancellationToken);
    }

    /// <summary>Return this device's backup private key when it belongs to <paramref name="backup"/>.</summary>
    private string? GetBackupSecretFor(KeyBackupVersion backup)
    {
        // Compare public keys
        if (backup.Algorithm != KeyBackupAlgorithms.MegolmV1 || !privateKeys.TryGetValue(SecretNames.MegolmBackup, out string? secret)) return null;
        // Return matching key
        return TryGetBackupPublicKey(secret) == backup.AuthData?["public_key"]?.GetValue<string>() ? secret : null;
    }

    /// <summary>Return the Curve25519 public key of a backup private key, or null when it is not one.</summary>
    private static string? TryGetBackupPublicKey(string secret)
    {
        // Derive
        try
        {
            return PkEncryption.GetPublicKey(secret);
        }
        catch (OlmException)
        {
            // Done; not a key
            return null;
        }
    }

    /// <summary>Encrypt sessions for the backup, each exported from its first known index.</summary>
    private KeysBackup CreateBackup(IReadOnlyList<InboundGroupSessionRecord> sessions, string publicKey)
    {
        // One entry per session, grouped by room
        KeysBackup backup = new();
        foreach (InboundGroupSessionRecord record in sessions)
        {
            using MegolmInboundSession session = MegolmInboundSession.FromPickle(record.Pickle, pickleKey);
            uint firstIndex = session.FirstKnownIndex;
            JsonObject data = new()
            {
                ["algorithm"] = EncryptionAlgorithms.Megolm,
                ["sender_key"] = record.SenderKey,
                ["sender_claimed_keys"] = new JsonObject { ["ed25519"] = record.SigningKey },
                ["forwarding_curve25519_key_chain"] = new JsonArray(),
                ["session_key"] = session.ExportAt(firstIndex),
            };
            PkMessage encrypted = PkEncryption.Encrypt(publicKey, data.ToJsonString());

            if (!backup.Rooms.TryGetValue(record.RoomId, out RoomKeyBackup? room)) backup.Rooms[record.RoomId] = room = new RoomKeyBackup();
            room.Sessions[record.SessionId] = new KeyBackupData
            {
                FirstMessageIndex = (int)firstIndex,
                ForwardedCount = record.Imported ? 1 : 0,
                IsVerified = false,
                SessionData = new JsonObject { ["ephemeral"] = encrypted.Ephemeral, ["ciphertext"] = encrypted.Ciphertext, ["mac"] = encrypted.Mac },
            };
        }
        // Return backup
        return backup;
    }

    /// <summary>Download backup <paramref name="version"/> and import sessions that are new or reach further back; return how many.</summary>
    private async Task<int> ImportBackupAsync(string version, string secret, CancellationToken cancellationToken)
    {
        // Every backed-up session
        KeysBackup backup = await transport.GetRoomKeysAsync(version, cancellationToken);
        int imported = 0;
        foreach ((string roomId, RoomKeyBackup room) in backup.Rooms)
            foreach ((string sessionId, KeyBackupData data) in room.Sessions)
            {
                // Decrypt; skip entries that are not for this key
                if (data.SessionData is not JsonObject sessionData || DecryptBackupEntry(secret, sessionData) is not JsonObject session) continue;
                if (session["algorithm"]?.GetValue<string>() != EncryptionAlgorithms.Megolm || session["session_key"]?.GetValue<string>() is not string sessionKey) continue;
                string senderKey = session["sender_key"]?.GetValue<string>() ?? "";
                string signingKey = session["sender_claimed_keys"]?["ed25519"]?.GetValue<string>() ?? "";
                if (await ImportSessionAsync(roomId, sessionId, sessionKey, senderKey, signingKey, backedUp: true, cancellationToken)) imported++;
            }
        // Return count
        return imported;
    }

    /// <summary>Decrypt one backup entry's <c>session_data</c>, or return null.</summary>
    private static JsonObject? DecryptBackupEntry(string secret, JsonObject sessionData)
    {
        // Assert fields
        if (sessionData["ciphertext"]?.GetValue<string>() is not string ciphertext || sessionData["mac"]?.GetValue<string>() is not string mac || sessionData["ephemeral"]?.GetValue<string>() is not string ephemeral) return null;
        try
        {
            return JsonNode.Parse(PkEncryption.Decrypt(secret, new PkMessage(ciphertext, mac, ephemeral))) as JsonObject;
        }
        catch (Exception ex) when (ex is OlmException or System.Text.Json.JsonException)
        {
            // Done; unreadable entry
            return null;
        }
    }

    /// <summary>Store an exported session from a backup or another device, unless a stored one reaches as far back; raise <see cref="RoomKeyReceived"/> when stored.</summary>
    /// <param name="roomId">Room of the session.</param>
    /// <param name="sessionId">Session ID, which must match the key.</param>
    /// <param name="exportedKey">Exported session key.</param>
    /// <param name="senderKey">Curve25519 key of the original sender's device.</param>
    /// <param name="signingKey">Ed25519 key the original sender claimed.</param>
    /// <param name="backedUp">Whether the session is already in the backup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<bool> ImportSessionAsync(string roomId, string sessionId, string exportedKey, string senderKey, string signingKey, bool backedUp, CancellationToken cancellationToken)
    {
        // Parse and check the ID
        MegolmInboundSession session;
        try
        {
            session = MegolmInboundSession.Import(exportedKey);
        }
        catch (OlmException)
        {
            return false;
        }
        using (session)
        {
            if (session.SessionId != sessionId) return false;

            // Keep the session that decrypts from earlier
            InboundGroupSessionRecord? existing = await store.LoadInboundGroupSessionAsync(roomId, sessionId, cancellationToken);
            if (existing is not null)
            {
                using MegolmInboundSession old = MegolmInboundSession.FromPickle(existing.Pickle, pickleKey);
                if (old.FirstKnownIndex <= session.FirstKnownIndex) return false;
            }
            await store.SaveInboundGroupSessionAsync(new InboundGroupSessionRecord { RoomId = roomId, SessionId = sessionId, SenderKey = senderKey, SigningKey = signingKey, Pickle = session.Pickle(pickleKey), BackedUp = backedUp, Imported = true }, cancellationToken);
        }

        // Announce
        RoomKeyReceived?.Invoke(roomId, sessionId);
        // Done
        return true;
    }
}
