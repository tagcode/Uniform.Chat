namespace Uniform.Matrix.Protocol.Crypto;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Outcome of <see cref="OlmMachine.DecryptRoomEventAsync"/>.</summary>
public enum DecryptionStatus
{
    /// <summary>The event was not encrypted and is returned as is.</summary>
    NotEncrypted,
    /// <summary>Decrypted.</summary>
    Decrypted,
    /// <summary>The room key has not arrived (yet); retry on <see cref="OlmMachine.RoomKeyReceived"/>.</summary>
    MissingKey,
    /// <summary>Decryption failed, e.g. a replayed or tampered message.</summary>
    Failed,
}

/// <summary>A timeline event after decryption.</summary>
public sealed record RoomEventDecryption
{
    /// <summary>Outcome.</summary>
    public required DecryptionStatus Status { get; init; }
    /// <summary>Decrypted event with its real type and content when <see cref="DecryptionStatus.Decrypted"/>, otherwise the original.</summary>
    public required ClientEvent Event { get; init; }
    /// <summary>Megolm session of the event, for retrying on <see cref="OlmMachine.RoomKeyReceived"/>.</summary>
    public string? SessionId { get; init; }
    /// <summary>Sender's device when its keys match the session, null when unknown.</summary>
    public DeviceInfo? SenderDevice { get; init; }
    /// <summary>Reason when not decrypted.</summary>
    public string? Error { get; init; }
}

// Room message encryption, key sharing and decryption.
public sealed partial class OlmMachine
{
    private const int DefaultRotationMessages = 100;
    private static readonly TimeSpan DefaultRotationPeriod = TimeSpan.FromDays(7);
    private readonly Dictionary<(string SessionId, uint Index), string> seenMessageIndices = [];

    /// <summary>Decrypt a Megolm <c>m.room.encrypted</c> event of <paramref name="roomId"/>; other events are returned unchanged.</summary>
    public async Task<RoomEventDecryption> DecryptRoomEventAsync(string roomId, ClientEvent clientEvent, CancellationToken cancellationToken = default)
    {
        // Only Megolm events
        if (clientEvent.Type != EventTypes.RoomEncrypted) return new RoomEventDecryption { Status = DecryptionStatus.NotEncrypted, Event = clientEvent };
        if (!clientEvent.TryGetContent(out EncryptedContent? content) || content.Algorithm != EncryptionAlgorithms.Megolm || content.SessionId is not string sessionId || content.Ciphertext.ValueKind != JsonValueKind.String)
            return new RoomEventDecryption { Status = DecryptionStatus.Failed, Event = clientEvent, Error = "not a Megolm event" };

        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Find the room key
            InboundGroupSessionRecord? record = await store.LoadInboundGroupSessionAsync(roomId, sessionId, cancellationToken);
            if (record is null)
            {
                // Our other devices may have it, or the device that sent it can send it again
                QueueKeyRequest(roomId, sessionId, content.SenderKey, clientEvent.Sender, content.DeviceId);
                return new RoomEventDecryption { Status = DecryptionStatus.MissingKey, Event = clientEvent, SessionId = sessionId, Error = "room key not received" };
            }

            // Decrypt; a key received from a later index cannot decrypt earlier messages
            using MegolmInboundSession session = MegolmInboundSession.FromPickle(record.Pickle, pickleKey);
            MegolmPlaintext plaintext;
            try
            {
                plaintext = session.Decrypt(content.Ciphertext.GetString()!);
            }
            catch (OlmException ex)
            {
                return new RoomEventDecryption { Status = DecryptionStatus.MissingKey, Event = clientEvent, SessionId = sessionId, Error = ex.Message };
            }

            // Reject replays: one index, one event
            if (seenMessageIndices.TryGetValue((sessionId, plaintext.MessageIndex), out string? seenEvent) && seenEvent != clientEvent.EventId)
                return new RoomEventDecryption { Status = DecryptionStatus.Failed, Event = clientEvent, SessionId = sessionId, Error = "message index reused" };
            seenMessageIndices[(sessionId, plaintext.MessageIndex)] = clientEvent.EventId;

            // Payload must be for this room
            if (JsonNode.Parse(plaintext.Plaintext) is not JsonObject payload || payload["room_id"]?.GetValue<string>() != roomId || payload["type"]?.GetValue<string>() is not string type || payload["content"] is not JsonObject decryptedContent)
                return new RoomEventDecryption { Status = DecryptionStatus.Failed, Event = clientEvent, SessionId = sessionId, Error = "payload is for another room or malformed" };

            // Relations stay in the clear so servers can aggregate them
            if (content.RelatesTo is not null && decryptedContent["m.relates_to"] is null) decryptedContent["m.relates_to"] = JsonSerializer.SerializeToNode(content.RelatesTo, MatrixJson.Options);

            // Sender's device, when the session came from one of the sender's known devices
            DeviceInfo? device = GetDevices(clientEvent.Sender).FirstOrDefault(d => d.Curve25519Key == record.SenderKey && d.Ed25519Key == record.SigningKey);
            ClientEvent decrypted = clientEvent with { Type = type, Content = JsonSerializer.SerializeToElement(decryptedContent) };
            // Return decrypted event
            return new RoomEventDecryption { Status = DecryptionStatus.Decrypted, Event = decrypted, SessionId = sessionId, SenderDevice = device };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Encrypt an event for <paramref name="roomId"/>, first sharing the room key with devices of <paramref name="memberIds"/> that lack it; return the <c>m.room.encrypted</c> content to send.</summary>
    /// <param name="roomId">Encrypted room.</param>
    /// <param name="eventType">Type of the event to encrypt, e.g. <see cref="EventTypes.RoomMessage"/>.</param>
    /// <param name="content">Event content; serialized with <see cref="MatrixJson.Options"/>.</param>
    /// <param name="memberIds">Joined and invited members, whose devices get the key.</param>
    /// <param name="settings">Room's <c>m.room.encryption</c> content, for rotation limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<EncryptedContent> EncryptRoomEventAsync(string roomId, string eventType, object content, IReadOnlyCollection<string> memberIds, EncryptionContent? settings = null, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Know every member's devices
            await UpdateDeviceListsAsync(memberIds, cancellationToken);

            // Current session, or a new one when due for rotation
            OutboundGroupSessionRecord record = await GetOrRotateOutboundSessionAsync(roomId, memberIds, settings, cancellationToken);
            using MegolmOutboundSession session = MegolmOutboundSession.FromPickle(record.Pickle, pickleKey);

            // Share the key with devices that lack it
            List<DeviceInfo> recipients = [.. memberIds.Distinct().SelectMany(GetDevices).Where(d => !(d.UserId == UserId && d.DeviceId == DeviceId))];
            HashSet<string> shared = [.. record.SharedWith];
            List<DeviceInfo> unshared = [.. recipients.Where(d => !shared.Contains(DeviceKey(d)))];
            if (unshared.Count > 0) await ShareRoomKeyAsync(roomId, session, unshared, shared, cancellationToken);

            // Encrypt the event with its room, so it cannot be replayed elsewhere
            JsonObject contentObject = JsonSerializer.SerializeToNode(content, content.GetType(), MatrixJson.Options) as JsonObject ?? [];
            JsonObject payload = new() { ["type"] = eventType, ["content"] = contentObject, ["room_id"] = roomId };
            string ciphertext = session.Encrypt(payload.ToJsonString());
            await store.SaveOutboundGroupSessionAsync(record with { Pickle = session.Pickle(pickleKey), MessageCount = record.MessageCount + 1, SharedWith = shared }, cancellationToken);

            // Return content; relations stay readable for servers
            Relation? relation = contentObject["m.relates_to"]?.Deserialize<Relation>(MatrixJson.Options);
            return new EncryptedContent
            {
                Algorithm = EncryptionAlgorithms.Megolm,
                Ciphertext = JsonSerializer.SerializeToElement(ciphertext),
                SenderKey = account.IdentityKeys.Curve25519,
                DeviceId = DeviceId,
                SessionId = session.SessionId,
                RelatesTo = relation,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forget the room's outbound session, so the next message starts a new one; e.g. after a member left.</summary>
    public async Task DiscardOutboundSessionAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            await store.DeleteOutboundGroupSessionAsync(roomId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Return the room's outbound session, starting a new one when there is none, it is too old or used, or someone it was shared with left.</summary>
    private async Task<OutboundGroupSessionRecord> GetOrRotateOutboundSessionAsync(string roomId, IReadOnlyCollection<string> memberIds, EncryptionContent? settings, CancellationToken cancellationToken)
    {
        // Keep a valid current session
        OutboundGroupSessionRecord? record = await store.LoadOutboundGroupSessionAsync(roomId, cancellationToken);
        int maxMessages = settings?.RotationPeriodMsgs is int messages and > 0 ? messages : DefaultRotationMessages;
        TimeSpan maxAge = settings?.RotationPeriodMs is long ms and > 0 ? TimeSpan.FromMilliseconds(ms) : DefaultRotationPeriod;
        HashSet<string> members = [.. memberIds];
        bool memberLeft = record?.SharedWith.Any(d => !members.Contains(d.Split('|')[0])) == true;
        if (record is not null && record.MessageCount < maxMessages && time.GetUtcNow() - record.CreatedAt < maxAge && !memberLeft) return record;

        // New session; keep its key ourselves, so our own messages decrypt in sync
        using MegolmOutboundSession session = MegolmOutboundSession.Create();
        using MegolmInboundSession own = MegolmInboundSession.FromSessionKey(session.SessionKey);
        await store.SaveInboundGroupSessionAsync(new InboundGroupSessionRecord { RoomId = roomId, SessionId = session.SessionId, SenderKey = account.IdentityKeys.Curve25519, SigningKey = account.IdentityKeys.Ed25519, Pickle = own.Pickle(pickleKey) }, cancellationToken);
        OutboundGroupSessionRecord created = new() { RoomId = roomId, SessionId = session.SessionId, Pickle = session.Pickle(pickleKey), CreatedAt = time.GetUtcNow() };
        await store.SaveOutboundGroupSessionAsync(created, cancellationToken);
        // Return new session
        return created;
    }

    /// <summary>Send the session key in <c>m.room_key</c> to each device over Olm, adding reached devices to <paramref name="shared"/>.</summary>
    private async Task ShareRoomKeyAsync(string roomId, MegolmOutboundSession session, IReadOnlyCollection<DeviceInfo> devices, HashSet<string> shared, CancellationToken cancellationToken)
    {
        // Olm session per device
        Dictionary<DeviceInfo, OlmSession> sessions = await GetOutboundOlmSessionsAsync(devices, cancellationToken);
        try
        {
            // Encrypt the key for each device
            JsonObject roomKey = new() { ["algorithm"] = EncryptionAlgorithms.Megolm, ["room_id"] = roomId, ["session_id"] = session.SessionId, ["session_key"] = session.SessionKey };
            Dictionary<string, Dictionary<string, object>> messages = [];
            foreach ((DeviceInfo device, OlmSession olm) in sessions)
            {
                if (!messages.TryGetValue(device.UserId, out Dictionary<string, object>? byDevice)) messages[device.UserId] = byDevice = [];
                byDevice[device.DeviceId] = EncryptForDevice(olm, device, EventTypes.RoomKey, roomKey);
            }

            // Send, then remember who has the key and the Olm sessions' new state
            if (messages.Count > 0) await transport.SendToDeviceAsync(EventTypes.RoomEncrypted, messages, cancellationToken);
            foreach ((DeviceInfo device, OlmSession olm) in sessions)
            {
                await SaveOlmSessionAsync(device.Curve25519Key, olm, cancellationToken);
                shared.Add(DeviceKey(device));
            }
            await SaveAccountAsync(cancellationToken);
        }
        finally
        {
            foreach (OlmSession olm in sessions.Values) olm.Dispose();
        }
    }

    /// <summary>Return <c>user|device</c> for <see cref="OutboundGroupSessionRecord.SharedWith"/>.</summary>
    private static string DeviceKey(DeviceInfo device) => $"{device.UserId}|{device.DeviceId}";
}
