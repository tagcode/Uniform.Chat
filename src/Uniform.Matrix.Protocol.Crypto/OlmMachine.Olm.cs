namespace Uniform.Matrix.Protocol.Crypto;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>A to-device event decrypted from Olm, with the keys of the device that sent it.</summary>
public sealed record DecryptedToDeviceEvent
{
    /// <summary>Event type of the decrypted payload, e.g. <see cref="EventTypes.RoomKey"/>.</summary>
    public required string Type { get; init; }
    /// <summary>Decrypted content.</summary>
    public required JsonObject Content { get; init; }
    /// <summary>Sending user.</summary>
    public required string Sender { get; init; }
    /// <summary>Curve25519 key of the sending device, proven by the Olm session.</summary>
    public required string SenderKey { get; init; }
    /// <summary>Ed25519 key the sending device claimed in the payload.</summary>
    public required string SenderSigningKey { get; init; }
}

// Olm sessions with other devices and to-device messages, including received room keys.
public sealed partial class OlmMachine
{
    /// <summary>Decrypt an Olm to-device event, creating an inbound session from a pre-key message; return null when it is not for this device or fails checks.</summary>
    private async Task<DecryptedToDeviceEvent?> DecryptToDeviceAsync(ToDeviceEvent ev, CancellationToken cancellationToken)
    {
        // Our ciphertext of an Olm event
        if (!ev.TryGetContent(out EncryptedContent? content) || content.Algorithm != EncryptionAlgorithms.Olm || content.SenderKey is not string senderKey) return null;
        if (content.Ciphertext.ValueKind != JsonValueKind.Object || !content.Ciphertext.TryGetProperty(account.IdentityKeys.Curve25519, out JsonElement ours)) return null;
        OlmMessage message = new(ours.GetProperty("type").GetInt32(), ours.GetProperty("body").GetString()!);

        // Existing session, else a new one from a pre-key message
        byte[]? plaintext = await DecryptWithExistingSessionAsync(senderKey, message, cancellationToken);
        if (plaintext is null && message.Type == 0) plaintext = await CreateInboundSessionAsync(senderKey, message, cancellationToken);
        if (plaintext is null) return null;

        // Payload must be from the claimed sender to this device
        if (JsonNode.Parse(plaintext) is not JsonObject payload) return null;
        bool addressed = payload["sender"]?.GetValue<string>() == ev.Sender
            && payload["recipient"]?.GetValue<string>() == UserId
            && payload["recipient_keys"]?["ed25519"]?.GetValue<string>() == account.IdentityKeys.Ed25519;
        if (!addressed || payload["type"]?.GetValue<string>() is not string type || payload["content"] is not JsonObject innerContent) return null;
        if (payload["keys"]?["ed25519"]?.GetValue<string>() is not string signingKey) return null;
        // Return decrypted event
        return new DecryptedToDeviceEvent { Type = type, Content = innerContent, Sender = ev.Sender, SenderKey = senderKey, SenderSigningKey = signingKey };
    }

    /// <summary>Try the stored sessions with <paramref name="senderKey"/>; for pre-key messages only the matching one.</summary>
    private async Task<byte[]?> DecryptWithExistingSessionAsync(string senderKey, OlmMessage message, CancellationToken cancellationToken)
    {
        // Candidate sessions
        IReadOnlyList<OlmSessionRecord> records = await store.LoadOlmSessionsAsync(senderKey, cancellationToken);
        string? preKeySession = message.Type == 0 ? OlmSession.PreKeySessionId(message.Body) : null;

        // First session that decrypts wins
        foreach (OlmSessionRecord record in records.OrderByDescending(r => r.LastUsed))
        {
            if (preKeySession is not null && record.SessionId != preKeySession) continue;
            using OlmSession session = OlmSession.FromPickle(record.Pickle, pickleKey);
            byte[] plaintext;
            try
            {
                plaintext = session.Decrypt(message);
            }
            catch (OlmException)
            {
                continue;
            }
            await SaveOlmSessionAsync(senderKey, session, cancellationToken);
            return plaintext;
        }
        // None decrypted
        return null;
    }

    /// <summary>Create an inbound session from a pre-key message and store it with the account, whose one-time key is now used.</summary>
    private async Task<byte[]?> CreateInboundSessionAsync(string senderKey, OlmMessage message, CancellationToken cancellationToken)
    {
        // Create, using up the one-time key
        OlmSession session;
        byte[] plaintext;
        try
        {
            (session, plaintext) = account.CreateInboundSession(senderKey, message.Body);
        }
        catch (OlmException)
        {
            return null;
        }

        // Store session and account
        using (session) await SaveOlmSessionAsync(senderKey, session, cancellationToken);
        await SaveAccountAsync(cancellationToken);
        // Return plaintext
        return plaintext;
    }

    /// <summary>Store a Megolm session received in <c>m.room_key</c>, keeping an existing one that reaches further back.</summary>
    private async Task AddRoomKeyAsync(DecryptedToDeviceEvent toDeviceEvent, CancellationToken cancellationToken)
    {
        // Assert a Megolm key
        RoomKeyContent? key = toDeviceEvent.Content.Deserialize<RoomKeyContent>(MatrixJson.Options);
        if (key is not { Algorithm: EncryptionAlgorithms.Megolm, RoomId: string roomId, SessionId: string sessionId, SessionKey: string sessionKey }) return;

        // Session ID must match the key
        using MegolmInboundSession session = MegolmInboundSession.FromSessionKey(sessionKey);
        if (session.SessionId != sessionId) return;

        // Keep the session that can decrypt from earlier
        InboundGroupSessionRecord? existing = await store.LoadInboundGroupSessionAsync(roomId, sessionId, cancellationToken);
        if (existing is not null)
        {
            using MegolmInboundSession old = MegolmInboundSession.FromPickle(existing.Pickle, pickleKey);
            if (old.FirstKnownIndex <= session.FirstKnownIndex) return;
        }

        // Store and announce
        await store.SaveInboundGroupSessionAsync(new InboundGroupSessionRecord { RoomId = roomId, SessionId = sessionId, SenderKey = toDeviceEvent.SenderKey, SigningKey = toDeviceEvent.SenderSigningKey, Pickle = session.Pickle(pickleKey) }, cancellationToken);
        RoomKeyReceived?.Invoke(roomId, sessionId);
    }

    /// <summary>Return an Olm session for each device, claiming one-time keys for devices without one; devices whose key cannot be claimed or verified are left out.</summary>
    private async Task<Dictionary<DeviceInfo, OlmSession>> GetOutboundOlmSessionsAsync(IReadOnlyCollection<DeviceInfo> devices, CancellationToken cancellationToken)
    {
        // Newest stored session per device
        Dictionary<DeviceInfo, OlmSession> sessions = [];
        List<DeviceInfo> missing = [];
        foreach (DeviceInfo device in devices)
        {
            OlmSessionRecord? record = (await store.LoadOlmSessionsAsync(device.Curve25519Key, cancellationToken)).MaxBy(r => r.LastUsed);
            if (record is null) missing.Add(device);
            else sessions[device] = OlmSession.FromPickle(record.Pickle, pickleKey);
        }
        if (missing.Count == 0) return sessions;

        // Claim one-time keys for the rest
        Dictionary<string, IReadOnlyList<string>> request = missing.GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(d => d.DeviceId)]);
        KeysClaimResponse claimed = await transport.ClaimKeysAsync(request, cancellationToken);
        foreach (DeviceInfo device in missing)
        {
            if (claimed.OneTimeKeys.GetValueOrDefault(device.UserId)?.GetValueOrDefault(device.DeviceId) is not { Count: > 0 } keys) continue;
            JsonObject keyObject = keys.Values.First();

            // Key must be signed by the device
            string? signature = CanonicalJson.GetSignature(keyObject, device.UserId, $"ed25519:{device.DeviceId}");
            if (keyObject["key"]?.GetValue<string>() is not string oneTimeKey || signature is null || !Ed25519.Verify(device.Ed25519Key, CanonicalJson.SigningForm(keyObject), signature)) continue;
            sessions[device] = account.CreateOutboundSession(device.Curve25519Key, oneTimeKey);
        }
        // Return sessions
        return sessions;
    }

    /// <summary>Encrypt a to-device event of <paramref name="type"/> for one device as <c>m.room.encrypted</c> content.</summary>
    private object EncryptForDevice(OlmSession session, DeviceInfo device, string type, JsonObject content)
    {
        // Payload binding sender, recipient and their keys
        JsonObject payload = new()
        {
            ["type"] = type,
            ["content"] = content.DeepClone(),
            ["sender"] = UserId,
            ["sender_device"] = DeviceId,
            ["keys"] = new JsonObject { ["ed25519"] = account.IdentityKeys.Ed25519 },
            ["recipient"] = device.UserId,
            ["recipient_keys"] = new JsonObject { ["ed25519"] = device.Ed25519Key },
        };
        OlmMessage message = session.Encrypt(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        // Return encrypted content
        return new JsonObject
        {
            ["algorithm"] = EncryptionAlgorithms.Olm,
            ["sender_key"] = account.IdentityKeys.Curve25519,
            ["ciphertext"] = new JsonObject { [device.Curve25519Key] = new JsonObject { ["type"] = message.Type, ["body"] = message.Body } },
        };
    }

    /// <summary>Store an Olm session's current state as used now.</summary>
    private Task SaveOlmSessionAsync(string senderKey, OlmSession session, CancellationToken cancellationToken) =>
        store.SaveOlmSessionAsync(new OlmSessionRecord { SenderKey = senderKey, SessionId = session.SessionId, Pickle = session.Pickle(pickleKey), LastUsed = time.GetUtcNow() }, cancellationToken);
}
