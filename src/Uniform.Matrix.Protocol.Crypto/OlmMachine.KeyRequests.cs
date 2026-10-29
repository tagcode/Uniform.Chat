namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Room key requests to our own devices and to the device that sent the key, answers to them, and secret requests between our own devices.
public sealed partial class OlmMachine
{
    private readonly Dictionary<string, KeyRequest> keyRequests = [];
    private readonly Dictionary<string, string> secretRequests = [];

    /// <summary>How long before a device whose message could not be decrypted gets another new session.</summary>
    public static readonly TimeSpan WedgedSessionRetry = TimeSpan.FromHours(1);
    private readonly Dictionary<string, DateTimeOffset> wedgedRecoveries = [];

    /// <summary>Raised with the sender, its Curve25519 key and the reason when a to-device message could not be decrypted.</summary>
    public event Action<string, string, string>? ToDeviceDecryptionFailed;

    /// <summary>Raised with the sender, its device and whether the message went out, after starting a new Olm session with a device whose message could not be decrypted.</summary>
    public event Action<string, string, bool>? WedgedSessionRecovered;

    /// <summary>Raised with the secret name when another of our devices sent a secret, e.g. after verification.</summary>
    public event Action<string>? SecretReceived;

    /// <summary>Note a missing room key, to request on the next sync from our other devices and from the device that sent it.</summary>
    private void QueueKeyRequest(string roomId, string sessionId, string? senderKey, string sender, string? senderDeviceId)
    {
        // Once per session
        if (keyRequests.ContainsKey(sessionId)) return;
        keyRequests[sessionId] = new KeyRequest(NewRequestId(), roomId, sessionId, senderKey ?? "") { Sender = sender, SenderDeviceId = senderDeviceId };
    }

    /// <summary>Send queued room key requests to our other devices, and to the device that sent the key when it is another user's.</summary>
    private async Task SendKeyRequestsAsync(CancellationToken cancellationToken)
    {
        // Assert unsent requests and other devices
        List<KeyRequest> unsent = [.. keyRequests.Values.Where(r => !r.Sent)];
        if (unsent.Count == 0) return;
        await UpdateDeviceListsAsync([UserId, .. unsent.Select(r => r.Sender).OfType<string>()], cancellationToken);
        List<string> devices = [.. GetDevices(UserId).Where(d => d.DeviceId != DeviceId).Select(d => d.DeviceId)];

        // One message per request to every other device, and to the sending device
        foreach (KeyRequest request in unsent)
        {
            request.Sent = true;
            string? senderDevice = request.Sender is string sender && sender != UserId
                ? request.SenderDeviceId ?? GetDevices(sender).FirstOrDefault(d => d.Curve25519Key == request.SenderKey)?.DeviceId
                : null;
            if (devices.Count == 0 && senderDevice is null) continue;
            JsonObject content = new()
            {
                ["action"] = "request",
                ["body"] = new JsonObject { ["algorithm"] = EncryptionAlgorithms.Megolm, ["room_id"] = request.RoomId, ["sender_key"] = request.SenderKey, ["session_id"] = request.SessionId },
                ["request_id"] = request.RequestId,
                ["requesting_device_id"] = DeviceId,
            };
            Dictionary<string, Dictionary<string, object>> messages = devices.Count > 0 ? ToDevices(UserId, devices, content) : [];
            if (senderDevice is not null) messages[request.Sender!] = new Dictionary<string, object> { [senderDevice] = content };
            await transport.SendToDeviceAsync(EventTypes.RoomKeyRequest, messages, cancellationToken);
        }
    }

    /// <summary>Answer a room key request with the session exported from its first known index: from one of our trusted devices for any key we have, or from another user's device for our own current session that we shared with it, e.g. when its copy got lost.</summary>
    private async Task HandleRoomKeyRequestAsync(string sender, JsonObject content, CancellationToken cancellationToken)
    {
        // A device asking for a Megolm key
        if (content["action"]?.GetValue<string>() != "request" || content["requesting_device_id"]?.GetValue<string>() is not string requester || (sender == UserId && requester == DeviceId)) return;
        if (content["body"] is not JsonObject body || body["algorithm"]?.GetValue<string>() != EncryptionAlgorithms.Megolm) return;
        if (body["room_id"]?.GetValue<string>() is not string roomId || body["session_id"]?.GetValue<string>() is not string sessionId) return;

        // Only keys we have, to our trusted devices or to those we shared our session with
        if (await store.LoadInboundGroupSessionAsync(roomId, sessionId, cancellationToken) is not InboundGroupSessionRecord record) return;
        DeviceInfo? device = sender == UserId ? await FindTrustedOwnDeviceAsync(d => d.DeviceId == requester, cancellationToken) : await FindSharedDeviceAsync(sender, requester, roomId, sessionId, record, cancellationToken);
        if (device is null) return;

        // Forward over Olm
        using MegolmInboundSession session = MegolmInboundSession.FromPickle(record.Pickle, pickleKey);
        JsonObject forwarded = new()
        {
            ["algorithm"] = EncryptionAlgorithms.Megolm,
            ["room_id"] = roomId,
            ["sender_key"] = record.SenderKey,
            ["session_id"] = sessionId,
            ["session_key"] = session.ExportAt(session.FirstKnownIndex),
            ["sender_claimed_ed25519_key"] = record.SigningKey,
            ["forwarding_curve25519_key_chain"] = new JsonArray(),
        };
        await SendEncryptedAsync(device, EventTypes.ForwardedRoomKey, forwarded, cancellationToken);
    }

    /// <summary>Import a forwarded room key that we requested, from one of our trusted devices or from the device that created the session, then cancel the request at the others.</summary>
    private async Task AddForwardedRoomKeyAsync(DecryptedToDeviceEvent toDeviceEvent, CancellationToken cancellationToken)
    {
        // A Megolm key we asked for
        JsonObject content = toDeviceEvent.Content;
        if (content["algorithm"]?.GetValue<string>() != EncryptionAlgorithms.Megolm || content["room_id"]?.GetValue<string>() is not string roomId || content["session_id"]?.GetValue<string>() is not string sessionId) return;
        if (content["session_key"]?.GetValue<string>() is not string sessionKey || !keyRequests.TryGetValue(sessionId, out KeyRequest? request) || request.RoomId != roomId) return;

        // From our trusted device, or the creator of the session itself, over its own Olm key
        bool fromCreator = toDeviceEvent.Sender == request.Sender && toDeviceEvent.SenderKey == request.SenderKey && content["sender_key"]?.GetValue<string>() == request.SenderKey;
        if (!fromCreator && (toDeviceEvent.Sender != UserId || await FindTrustedOwnDeviceAsync(d => d.Curve25519Key == toDeviceEvent.SenderKey, cancellationToken) is null)) return;

        // Import, then tell the other devices to stop
        string senderKey = content["sender_key"]?.GetValue<string>() ?? request.SenderKey;
        string signingKey = content["sender_claimed_ed25519_key"]?.GetValue<string>() ?? "";
        if (!await ImportSessionAsync(roomId, sessionId, sessionKey, senderKey, signingKey, backedUp: false, cancellationToken)) return;
        keyRequests.Remove(sessionId);
        List<string> others = [.. GetDevices(UserId).Where(d => d.DeviceId != DeviceId).Select(d => d.DeviceId)];
        JsonObject cancel = new() { ["action"] = "request_cancellation", ["request_id"] = request.RequestId, ["requesting_device_id"] = DeviceId };
        if (others.Count > 0) await transport.SendToDeviceAsync(EventTypes.RoomKeyRequest, ToDevices(UserId, others, cancel), cancellationToken);
    }

    /// <summary>Ask one of our devices for the secrets this device lacks, e.g. right after verifying it.</summary>
    private async Task RequestMissingSecretsAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        // Secrets not held, or not matching the published keys
        UserIdentity? identity = GetIdentity(UserId);
        List<string> missing = [];
        if (GetPrivateKey(SecretNames.CrossSigningMaster, identity?.MasterKey) is null) missing.Add(SecretNames.CrossSigningMaster);
        if (GetPrivateKey(SecretNames.CrossSigningSelfSigning, identity?.SelfSigningKey) is null) missing.Add(SecretNames.CrossSigningSelfSigning);
        if (GetPrivateKey(SecretNames.CrossSigningUserSigning, identity?.UserSigningKey) is null) missing.Add(SecretNames.CrossSigningUserSigning);
        if (!privateKeys.ContainsKey(SecretNames.MegolmBackup)) missing.Add(SecretNames.MegolmBackup);

        // One request each
        foreach (string name in missing)
        {
            string requestId = NewRequestId();
            secretRequests[requestId] = name;
            JsonObject content = new() { ["name"] = name, ["action"] = "request", ["requesting_device_id"] = DeviceId, ["request_id"] = requestId };
            await transport.SendToDeviceAsync(EventTypes.SecretRequest, ToDevices(UserId, [device.DeviceId], content), cancellationToken);
        }
    }

    /// <summary>Answer a secret request from one of our trusted devices, when we hold a current copy of the secret.</summary>
    private async Task HandleSecretRequestAsync(string sender, JsonObject content, CancellationToken cancellationToken)
    {
        // Our other device asking for a known secret
        if (sender != UserId || content["action"]?.GetValue<string>() != "request" || content["requesting_device_id"]?.GetValue<string>() is not string requester || requester == DeviceId) return;
        if (content["name"]?.GetValue<string>() is not string name || content["request_id"]?.GetValue<string>() is not string requestId) return;

        // Only to trusted devices
        if (await FindTrustedOwnDeviceAsync(d => d.DeviceId == requester, cancellationToken) is not DeviceInfo device) return;

        // Current secret only
        UserIdentity? identity = GetIdentity(UserId);
        string? secret = name switch
        {
            SecretNames.CrossSigningMaster => GetPrivateKey(name, identity?.MasterKey),
            SecretNames.CrossSigningSelfSigning => GetPrivateKey(name, identity?.SelfSigningKey),
            SecretNames.CrossSigningUserSigning => GetPrivateKey(name, identity?.UserSigningKey),
            SecretNames.MegolmBackup => privateKeys.GetValueOrDefault(name),
            _ => null,
        };
        if (secret is null) return;
        await SendEncryptedAsync(device, EventTypes.SecretSend, new JsonObject { ["request_id"] = requestId, ["secret"] = secret }, cancellationToken);
    }

    /// <summary>Keep a secret we requested, from one of our trusted devices, when it matches the published keys.</summary>
    private async Task ReceiveSecretAsync(DecryptedToDeviceEvent toDeviceEvent, CancellationToken cancellationToken)
    {
        // Our request, answered by our trusted device
        if (toDeviceEvent.Sender != UserId || toDeviceEvent.Content["request_id"]?.GetValue<string>() is not string requestId || toDeviceEvent.Content["secret"]?.GetValue<string>() is not string secret) return;
        if (!secretRequests.Remove(requestId, out string? name)) return;
        if (await FindTrustedOwnDeviceAsync(d => d.Curve25519Key == toDeviceEvent.SenderKey, cancellationToken) is null) return;

        // Backup key: checked against the server's backup, then restored
        if (name == SecretNames.MegolmBackup)
        {
            if (await UseBackupKeyAsync(secret, cancellationToken) is not null) SecretReceived?.Invoke(name);
            return;
        }

        // Cross-signing key: must match the published one
        UserIdentity? identity = GetIdentity(UserId);
        string? expected = name switch
        {
            SecretNames.CrossSigningMaster => identity?.MasterKey,
            SecretNames.CrossSigningSelfSigning => identity?.SelfSigningKey,
            SecretNames.CrossSigningUserSigning => identity?.UserSigningKey,
            _ => null,
        };
        if (expected is null || TryGetPublicKey(secret) != expected) return;
        await SavePrivateKeyAsync(name, secret, cancellationToken);

        // A master key makes the identity trusted; a self-signing key signs this device
        if (name == SecretNames.CrossSigningMaster) await UpdateIdentityAsync(UserId, i => i with { Trusted = true }, cancellationToken);
        if (name == SecretNames.CrossSigningSelfSigning) await SignOwnDevicesAsync([DeviceId], cancellationToken);
        SecretReceived?.Invoke(name);
    }

    /// <summary>Return our own device matching <paramref name="match"/> when it is trusted; query again once when it is unknown or not yet trusted, as a new device's signatures may be newer than our list.</summary>
    private async Task<DeviceInfo?> FindTrustedOwnDeviceAsync(Func<DeviceInfo, bool> match, CancellationToken cancellationToken)
    {
        // Known list first
        await UpdateDeviceListsAsync([UserId], cancellationToken);
        DeviceInfo? device = GetDevices(UserId).FirstOrDefault(match);
        if (device is not null && IsTrustedOwnDevice(device)) return device;

        // Fresh list
        await RefreshDeviceListAsync(UserId, cancellationToken);
        device = GetDevices(UserId).FirstOrDefault(match);
        // Return trusted device
        return device is not null && IsTrustedOwnDevice(device) ? device : null;
    }

    /// <summary>Return another user's device that asks for our own current session of a room, when we shared that session with it.</summary>
    private async Task<DeviceInfo?> FindSharedDeviceAsync(string userId, string deviceId, string roomId, string sessionId, InboundGroupSessionRecord record, CancellationToken cancellationToken)
    {
        // Our session, still current, and shared with the device
        if (record.SenderKey != account.IdentityKeys.Curve25519) return null;
        if (await store.LoadOutboundGroupSessionAsync(roomId, cancellationToken) is not OutboundGroupSessionRecord outbound || outbound.SessionId != sessionId) return null;
        if (!outbound.SharedWith.Contains($"{userId}|{deviceId}")) return null;
        await UpdateDeviceListsAsync([userId], cancellationToken);
        // Return the device
        return GetDevices(userId).FirstOrDefault(d => d.DeviceId == deviceId);
    }

    /// <summary>Encrypt and send one to-device event to <paramref name="device"/> over Olm; return false when no Olm session could be made.</summary>
    /// <param name="device">Recipient.</param>
    /// <param name="type">Event type.</param>
    /// <param name="content">Event content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="newSession">Whether to start a new Olm session rather than use the newest, e.g. to recover a wedged one.</param>
    private async Task<bool> SendEncryptedAsync(DeviceInfo device, string type, JsonObject content, CancellationToken cancellationToken, bool newSession = false)
    {
        // Olm session
        Dictionary<DeviceInfo, OlmSession> sessions = await GetOutboundOlmSessionsAsync([device], cancellationToken, newSession);
        try
        {
            if (!sessions.TryGetValue(device, out OlmSession? session)) return false;

            // Send, then store the session's new state
            object encrypted = EncryptForDevice(session, device, type, content);
            await transport.SendToDeviceAsync(EventTypes.RoomEncrypted, ToDevices(device.UserId, [device.DeviceId], encrypted), cancellationToken);
            await SaveOlmSessionAsync(device.Curve25519Key, session, cancellationToken);
            await SaveAccountAsync(cancellationToken);
            // Done
            return true;
        }
        finally
        {
            foreach (OlmSession session in sessions.Values) session.Dispose();
        }
    }

    /// <summary>Build a to-device message map with the same content for several devices of one user.</summary>
    private static Dictionary<string, Dictionary<string, object>> ToDevices(string userId, IEnumerable<string> deviceIds, object content) =>
        new() { [userId] = deviceIds.ToDictionary(d => d, _ => content) };

    /// <summary>Return a random request or transaction ID.</summary>
    private static string NewRequestId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>An outgoing room key request.</summary>
    /// <param name="RequestId">ID shared by the request and its cancellation.</param>
    /// <param name="RoomId">Room of the session.</param>
    /// <param name="SessionId">Missing session.</param>
    /// <param name="SenderKey">Curve25519 key of the session's sender, as in the event.</param>
    private sealed record KeyRequest(string RequestId, string RoomId, string SessionId, string SenderKey)
    {
        /// <summary>User who sent the event that needs the key; null when unknown.</summary>
        public string? Sender { get; init; }
        /// <summary>Device that sent it, from the event; null when the event does not say.</summary>
        public string? SenderDeviceId { get; init; }
        /// <summary>Whether the request has been sent.</summary>
        public bool Sent { get; set; }
    }
}
