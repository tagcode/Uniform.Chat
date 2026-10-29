namespace Uniform.Matrix.Client.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>End-to-end encryption of the client: owns the <see cref="OlmMachine"/>, decrypts syncs and encrypts outgoing events.</summary>
/// <remarks>When the stored encryption state cannot be read, e.g. with another <c>crypto.key</c>, the client runs without encryption: encrypted messages stay unreadable and sending to encrypted rooms is refused.</remarks>
public sealed class CryptoService : IDisposable
{
    private readonly EfCryptoStore store;
    private readonly AccountPaths paths;
    private readonly ILogger<CryptoService> logger;
    private readonly ConcurrentQueue<(string RoomId, string SessionId)> receivedKeys = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OlmMachine? machine;

    /// <summary>Create the service; encryption starts with <see cref="StartAsync"/>.</summary>
    public CryptoService(EfCryptoStore store, AccountPaths paths, ILogger<CryptoService> logger)
    {
        // Store dependencies
        this.store = store;
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>Whether encryption works; false before <see cref="StartAsync"/> or when the stored state cannot be read.</summary>
    public bool Available => machine is not null;
    /// <summary>Completes once <see cref="StartAsync"/> has finished, with or without encryption; check <see cref="Available"/> after it.</summary>
    public Task WhenStarted => started.Task;
    /// <summary>Why encryption is not available, or null.</summary>
    public string? UnavailableReason { get; private set; } = "Not started.";
    /// <summary>This device's identity keys, when available.</summary>
    public IdentityKeys? IdentityKeys => machine?.IdentityKeys;
    /// <summary>This device's ID, when available.</summary>
    public string? DeviceId => machine?.DeviceId;
    /// <summary>Verifications in progress.</summary>
    public IReadOnlyList<Verification> Verifications => machine?.Verifications ?? [];

    /// <summary>Raised when a verification starts, changes or ends; raised off the UI thread.</summary>
    public event Action<Verification>? VerificationChanged;

    /// <summary>Raised when a verification message is dropped, with its sender, device and why, see <see cref="OlmMachine.VerificationIgnored"/>.</summary>
    public event Action<string, string?, string>? VerificationIgnored;

    /// <summary>Load or create the device's encryption account and upload its keys; log and continues without encryption on failure.</summary>
    public async Task StartAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Load the account of this device
        try
        {
            if (await store.ResetForDeviceAsync(client.DeviceId!, cancellationToken)) logger.LogInformation("Encryption state of a previous device was cleared");
            machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), store, client.UserId!, client.DeviceId!, LoadOrCreatePickleKey(), cancellationToken: cancellationToken);
            machine.RoomKeyReceived += (roomId, sessionId) => receivedKeys.Enqueue((roomId, sessionId));
            machine.VerificationChanged += v =>
            {
                string sentTo = v.WeRequested && v.State == VerificationState.Requested ? $", sent to {(v.RequestedDeviceIds.Count == 0 ? "no device" : string.Join(", ", v.RequestedDeviceIds))}" : "";
                logger.LogInformation("Verification {TransactionId} with {UserId} {DeviceId}: {State}{SentTo}{Cancel}", v.TransactionId, v.OtherUserId, v.OtherDeviceId ?? "(any device)", v.State, sentTo, v.CancelCode is null ? "" : $" ({v.CancelCode}: {v.CancelReason})");
                VerificationChanged?.Invoke(v);
            };
            machine.VerificationIgnored += (sender, device, reason) =>
            {
                logger.LogWarning("Verification message of {UserId} {DeviceId} ignored: {Reason}", sender, device ?? "(no device)", reason);
                VerificationIgnored?.Invoke(sender, device, reason);
            };
            machine.SecretReceived += name => logger.LogInformation("Received secret {Name} from another device", name);
            machine.ToDeviceDecryptionFailed += (sender, senderKey, reason) => logger.LogWarning("To-device message from {Sender} ({SenderKey}) not decrypted: {Reason}", sender, senderKey, reason);
            machine.WedgedSessionRecovered += (sender, deviceId, sent) => logger.LogWarning("Started a new Olm session with {Sender} {DeviceId} after an undecryptable message; {Result}", sender, deviceId, sent ? "sent m.dummy over it" : "no one-time key could be claimed");
            await machine.UploadInitialKeysAsync(cancellationToken);
            UnavailableReason = null;
            logger.LogInformation("Encryption ready on device {DeviceId}, fingerprint {Fingerprint}", client.DeviceId, machine.IdentityKeys.Ed25519);
        }
        catch (OlmException ex)
        {
            machine = null;
            UnavailableReason = $"The stored encryption state cannot be read ({ex.Message}). Was {paths.CryptoKeyPath} replaced?";
            logger.LogWarning("Encryption unavailable: {Reason}", UnavailableReason);
        }
        finally
        {
            // Waiters go on either way
            started.TrySetResult();
        }
    }

    /// <summary>Process the encryption parts of <paramref name="sync"/> and return it with Megolm timeline events decrypted where keys are known.</summary>
    public async Task<SyncResponse> DecryptSyncAsync(SyncResponse sync, CancellationToken cancellationToken)
    {
        // Assert encryption
        if (machine is null) return sync;

        // To-device messages and keys first, as they bring room keys
        await machine.ReceiveSyncAsync(sync, cancellationToken);
        await BackUpAsync(cancellationToken);

        // Replace decryptable timeline events
        Dictionary<string, JoinedRoom> joined = [];
        foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join) joined[roomId] = room with { Timeline = room.Timeline with { Events = await DecryptAllAsync(roomId, room.Timeline.Events, cancellationToken) } };
        Dictionary<string, LeftRoom> left = [];
        foreach ((string roomId, LeftRoom room) in sync.Rooms.Leave) left[roomId] = room with { Timeline = room.Timeline with { Events = await DecryptAllAsync(roomId, room.Timeline.Events, cancellationToken) } };
        // Return decrypted sync
        return sync with { Rooms = sync.Rooms with { Join = joined, Leave = left } };
    }

    /// <summary>Return room keys received since the last call, to decrypt stored events that were waiting for them.</summary>
    public IReadOnlyList<(string RoomId, string SessionId)> TakeReceivedKeys()
    {
        // Drain the queue
        List<(string, string)> keys = [];
        while (receivedKeys.TryDequeue(out (string, string) key)) keys.Add(key);
        // Return keys
        return keys;
    }

    /// <summary>Decrypt one event; return it unchanged when not encrypted or not decryptable.</summary>
    public async Task<ClientEvent> DecryptAsync(string roomId, ClientEvent clientEvent, CancellationToken cancellationToken)
    {
        // Assert encryption
        if (machine is null || clientEvent.Type != EventTypes.RoomEncrypted) return clientEvent;
        RoomEventDecryption result = await machine.DecryptRoomEventAsync(roomId, clientEvent, cancellationToken);
        // Return decrypted or original
        if (result.Status is DecryptionStatus.Failed) logger.LogWarning("Event {EventId} in {RoomId} did not decrypt: {Error}", clientEvent.EventId, roomId, result.Error);
        return result.Event;
    }

    /// <summary>Encrypt an event for an encrypted room; throw <see cref="InvalidOperationException"/> when encryption is unavailable, so nothing is sent in the clear.</summary>
    /// <param name="roomId">Encrypted room.</param>
    /// <param name="eventType">Type of the event to encrypt.</param>
    /// <param name="content">Event content.</param>
    /// <param name="memberIds">Joined and invited members, whose devices get the room key.</param>
    /// <param name="settings">Room's <c>m.room.encryption</c> content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EncryptedContent> EncryptAsync(string roomId, string eventType, JsonElement content, IReadOnlyCollection<string> memberIds, EncryptionContent? settings, CancellationToken cancellationToken)
    {
        // Refuse rather than leak
        if (machine is null) throw new InvalidOperationException($"Encryption is not available: {UnavailableReason}");
        // Return encrypted content
        return machine.EncryptRoomEventAsync(roomId, eventType, content, memberIds, settings, cancellationToken);
    }

    /// <summary>Report cross-signing, backup and secret storage state of the account.</summary>
    public Task<SecurityStatus> GetSecurityStatusAsync(CancellationToken cancellationToken = default) => Machine.GetSecurityStatusAsync(cancellationToken);

    /// <summary>Set up cross-signing, key backup and secret storage; return the recovery key. See <see cref="OlmMachine.SetUpRecoveryAsync"/>.</summary>
    public Task<string> SetUpRecoveryAsync(string? password, CancellationToken cancellationToken = default) => Machine.SetUpRecoveryAsync(password, cancellationToken);

    /// <summary>Restore keys from secret storage with the recovery key and import the backup. See <see cref="OlmMachine.RecoverAsync"/>.</summary>
    public Task<RecoveryResult> RecoverAsync(string recoveryKey, CancellationToken cancellationToken = default) => Machine.RecoverAsync(recoveryKey, cancellationToken);

    /// <summary>Return the user's devices signed by their current self-signing key, which can vouch for others of the account, whether or not this device trusts the identity; none when encryption is off.</summary>
    public IReadOnlyList<DeviceInfo> GetSignedDevices(string userId) => machine is null ? [] : [.. machine.GetDevices(userId).Where(machine.IsSignedByIdentity)];

    /// <summary>Return whether this device has verified <paramref name="userId"/>: their cross-signing identity is trusted, or, without one, every device of theirs known here is verified; false when encryption is off.</summary>
    public bool IsUserVerified(string userId)
    {
        // Identity first, else each device
        if (machine is null) return false;
        if (machine.GetIdentity(userId) is UserIdentity identity) return machine.IsIdentityTrusted(identity);
        IReadOnlyList<DeviceInfo> devices = machine.GetDevices(userId);
        // Return whether all devices are verified
        return devices.Count > 0 && devices.All(d => machine.GetDeviceTrust(d) is DeviceTrust.Verified or DeviceTrust.CrossSigned);
    }

    /// <summary>Return the trust of a known device, or null when the device is unknown or encryption is off.</summary>
    public DeviceTrust? GetDeviceTrust(string userId, string deviceId) =>
        machine?.GetDevices(userId).FirstOrDefault(d => d.DeviceId == deviceId) is DeviceInfo device ? machine.GetDeviceTrust(device) : null;

    /// <summary>Return the known devices of <paramref name="userId"/>; call <see cref="TrackUserAsync"/> first.</summary>
    public IReadOnlyList<DeviceInfo> GetDevices(string userId) => machine?.GetDevices(userId) ?? [];

    /// <summary>Fetch the devices of <paramref name="userId"/>, so their trust is known.</summary>
    public Task TrackUserAsync(string userId, CancellationToken cancellationToken = default) => Machine.TrackUsersAsync([userId], cancellationToken);

    /// <summary>Start verifying a device of <paramref name="userId"/>, or all of them.</summary>
    public Task<Verification> RequestVerificationAsync(string userId, string? deviceId, CancellationToken cancellationToken = default) => Machine.RequestVerificationAsync(userId, deviceId, cancellationToken);

    /// <summary>Accept an incoming verification request.</summary>
    public Task AcceptVerificationAsync(Verification verification, CancellationToken cancellationToken = default) => Machine.AcceptVerificationAsync(verification, cancellationToken);

    /// <summary>Confirm that the emoji match.</summary>
    public Task ConfirmVerificationAsync(Verification verification, CancellationToken cancellationToken = default) => Machine.ConfirmVerificationAsync(verification, cancellationToken);

    /// <summary>Cancel a verification; <c>m.mismatched_sas</c> when the emoji differ.</summary>
    public Task CancelVerificationAsync(Verification verification, string code = "m.user", CancellationToken cancellationToken = default) => Machine.CancelVerificationAsync(verification, code, cancellationToken);

    /// <summary>Upload new room keys to the backup; failures are logged, as the next sync retries.</summary>
    private async Task BackUpAsync(CancellationToken cancellationToken)
    {
        // Upload what is new
        try
        {
            int count = await Machine.BackUpRoomKeysAsync(cancellationToken);
            if (count > 0) logger.LogInformation("Backed up {Count} room keys", count);
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException)
        {
            logger.LogWarning("Key backup failed: {Error}", ex.Message);
        }
    }

    /// <summary>The machine; throws when encryption is unavailable.</summary>
    private OlmMachine Machine => machine ?? throw new InvalidOperationException($"Encryption is not available: {UnavailableReason}");

    /// <summary>Format an Ed25519 key in groups of four, as clients show fingerprints for comparison.</summary>
    public static string FormatFingerprint(string key) => string.Join(' ', key.Chunk(4).Select(c => new string(c)));

    /// <summary>Return the pickle key from <see cref="AccountPaths.CryptoKeyPath"/>, creating a random one readable only by the user.</summary>
    private byte[] LoadOrCreatePickleKey()
    {
        // Existing key
        if (File.Exists(paths.CryptoKeyPath))
        {
            byte[] existing = File.ReadAllBytes(paths.CryptoKeyPath);
            if (existing.Length == 32) return existing;
            throw new InvalidOperationException($"{paths.CryptoKeyPath} is not a 32-byte key.");
        }

        // New random key, private to the user
        Directory.CreateDirectory(paths.Directory);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(paths.CryptoKeyPath, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(paths.CryptoKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Return key
        return key;
    }

    /// <summary>Decrypt each Megolm event of a list.</summary>
    private async Task<IReadOnlyList<ClientEvent>> DecryptAllAsync(string roomId, IReadOnlyList<ClientEvent> events, CancellationToken cancellationToken)
    {
        // Decrypt in order
        List<ClientEvent> result = new(events.Count);
        foreach (ClientEvent ev in events) result.Add(await DecryptAsync(roomId, ev, cancellationToken));
        // Return events
        return result;
    }

    /// <summary>Wipe key material in memory.</summary>
    public void Dispose() => machine?.Dispose();
}
