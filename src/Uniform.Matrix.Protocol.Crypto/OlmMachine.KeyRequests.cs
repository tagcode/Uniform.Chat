namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Room key and secret requests between the user's own devices.
public sealed partial class OlmMachine
{
    private readonly Dictionary<string, KeyRequest> keyRequests = [];
    private readonly Dictionary<string, string> secretRequests = [];

    /// <summary>Raised with the secret name when another of our devices sent a secret, e.g. after verification.</summary>
    public event Action<string>? SecretReceived;

    /// <summary>Note a missing room key, to request from our other devices on the next sync.</summary>
    private void QueueKeyRequest(string roomId, string sessionId, string? senderKey)
    {
        // Once per session
        if (keyRequests.ContainsKey(sessionId)) return;
        keyRequests[sessionId] = new KeyRequest(NewRequestId(), roomId, sessionId, senderKey ?? "");
    }

    /// <summary>Send queued room key requests to our other devices.</summary>
    private async Task SendKeyRequestsAsync(CancellationToken cancellationToken)
    {
        // Assert unsent requests and other devices
        List<KeyRequest> unsent = [.. keyRequests.Values.Where(r => !r.Sent)];
        if (unsent.Count == 0) return;
        await UpdateDeviceListsAsync([UserId], cancellationToken);
        List<string> devices = [.. GetDevices(UserId).Where(d => d.DeviceId != DeviceId).Select(d => d.DeviceId)];

        // One message per request to every other device
        foreach (KeyRequest request in unsent)
        {
            request.Sent = true;
            if (devices.Count == 0) continue;
            JsonObject content = new()
            {
                ["action"] = "request",
                ["body"] = new JsonObject { ["algorithm"] = EncryptionAlgorithms.Megolm, ["room_id"] = request.RoomId, ["sender_key"] = request.SenderKey, ["session_id"] = request.SessionId },
                ["request_id"] = request.RequestId,
                ["requesting_device_id"] = DeviceId,
            };
            await transport.SendToDeviceAsync(EventTypes.RoomKeyRequest, ToDevices(UserId, devices, content), cancellationToken);
        }
    }

    /// <summary>Answer a room key request from one of our trusted devices with the session exported from its first known index.</summary>
    private async Task HandleRoomKeyRequestAsync(string sender, JsonObject content, CancellationToken cancellationToken)
    {
        // Our other device asking for a Megolm key
        if (sender != UserId || content["action"]?.GetValue<string>() != "request" || content["requesting_device_id"]?.GetValue<string>() is not string requester || requester == DeviceId) return;
        if (content["body"] is not JsonObject body || body["algorithm"]?.GetValue<string>() != EncryptionAlgorithms.Megolm) return;
        if (body["room_id"]?.GetValue<string>() is not string roomId || body["session_id"]?.GetValue<string>() is not string sessionId) return;

        // Only to trusted devices, and only keys we have
        if (await FindTrustedOwnDeviceAsync(d => d.DeviceId == requester, cancellationToken) is not DeviceInfo device) return;
        if (await store.LoadInboundGroupSessionAsync(roomId, sessionId, cancellationToken) is not InboundGroupSessionRecord record) return;

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

    /// <summary>Import a forwarded room key that we requested, from one of our trusted devices, then cancel the request at the others.</summary>
    private async Task AddForwardedRoomKeyAsync(DecryptedToDeviceEvent toDeviceEvent, CancellationToken cancellationToken)
    {
        // From our trusted device
        if (toDeviceEvent.Sender != UserId || await FindTrustedOwnDeviceAsync(d => d.Curve25519Key == toDeviceEvent.SenderKey, cancellationToken) is null) return;

        // A Megolm key we asked for
        JsonObject content = toDeviceEvent.Content;
        if (content["algorithm"]?.GetValue<string>() != EncryptionAlgorithms.Megolm || content["room_id"]?.GetValue<string>() is not string roomId || content["session_id"]?.GetValue<string>() is not string sessionId) return;
        if (content["session_key"]?.GetValue<string>() is not string sessionKey || !keyRequests.TryGetValue(sessionId, out KeyRequest? request) || request.RoomId != roomId) return;

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

    /// <summary>Encrypt and send one to-device event to <paramref name="device"/> over Olm; return false when no Olm session could be made.</summary>
    private async Task<bool> SendEncryptedAsync(DeviceInfo device, string type, JsonObject content, CancellationToken cancellationToken)
    {
        // Olm session
        Dictionary<DeviceInfo, OlmSession> sessions = await GetOutboundOlmSessionsAsync([device], cancellationToken);
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
        /// <summary>Whether the request has been sent.</summary>
        public bool Sent { get; set; }
    }
}
