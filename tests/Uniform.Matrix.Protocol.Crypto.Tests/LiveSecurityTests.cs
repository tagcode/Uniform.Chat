namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using Uniform.Matrix.Client;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Cross-signing, key backup, key requests and verification against the real test homeserver, with the second and third test accounts; exclude with <c>dotnet test -- --filter-not-trait "Category=Live"</c>.</summary>
/// <remarks>The first run sets up recovery on the accounts and saves the recovery keys in <c>testclient-recovery.txt</c>; later runs recover with them. Tests remove only rooms and devices they created.</remarks>
[Trait("Category", "Live")]
public class LiveSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 1 } } }.ToJson();

    /// <summary>A message backed up by one device is read by a new login that only has the recovery key; the new login is cross-signed.</summary>
    [Fact]
    public async Task CanReadOldMessageOnNewLogin()
    {
        // Arrange: a recovered device sends a message only it has the key for, and backs it up
        (string user, string password) = Account(1);
        using HttpClient http = NewHttp();
        await using LiveDevice first = await LiveDevice.LoginAsync(http, user, password, "backup sender");
        string recoveryKey = await EnsureRecoveryAsync(first, password);
        string? roomId = null;
        try
        {
            roomId = await CreateEncryptedRoomAsync(first.Client);
            string secret = $"backed up {Guid.NewGuid():N}";
            EncryptedContent content = await first.Machine.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, Text(secret), [], cancellationToken: Ct);
            string eventId = await first.Client.SendEventAsync(roomId, EventTypes.RoomEncrypted, content, cancellationToken: Ct);
            Assert.True(await first.Machine.BackUpRoomKeysAsync(Ct) >= 1);

            // Act: a new login recovers
            await using LiveDevice second = await LiveDevice.LoginAsync(http, user, password, "backup reader");
            RecoveryResult result = await second.Machine.RecoverAsync(recoveryKey, Ct);

            // Assert
            Assert.True(result.CrossSigningRestored && result.BackupRestored);
            Assert.True(result.KeysImported >= 1);
            RoomEventDecryption decrypted = await second.Machine.DecryptRoomEventAsync(roomId, await second.Client.GetEventAsync(roomId, eventId, Ct), Ct);
            Assert.Equal(DecryptionStatus.Decrypted, decrypted.Status);
            Assert.True(decrypted.Event.TryGetContent(out MessageContent? message));
            Assert.Equal(secret, message.Body);
            Assert.True((await second.Machine.GetSecurityStatusAsync(Ct)).ThisDeviceCrossSigned);
        }
        finally
        {
            await RemoveRoomAsync(first.Client, roomId);
        }
    }

    /// <summary>A cross-signed device asks another for a key it missed, and gets it forwarded over Olm.</summary>
    [Fact]
    public async Task CanForwardKeyBetweenOwnDevices()
    {
        // Arrange: two recovered devices; the key stays on the first
        (string user, string password) = Account(1);
        using HttpClient http = NewHttp();
        await using LiveDevice sender = await LiveDevice.LoginAsync(http, user, password, "key holder");
        string recoveryKey = await EnsureRecoveryAsync(sender, password);
        await using LiveDevice requester = await LiveDevice.LoginAsync(http, user, password, "key requester");
        await requester.Machine.RecoverAsync(recoveryKey, Ct);
        string? roomId = null;
        try
        {
            roomId = await CreateEncryptedRoomAsync(sender.Client);
            string secret = $"forwarded {Guid.NewGuid():N}";
            EncryptedContent content = await sender.Machine.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, Text(secret), [], cancellationToken: Ct);
            string eventId = await sender.Client.SendEventAsync(roomId, EventTypes.RoomEncrypted, content, cancellationToken: Ct);
            ClientEvent ev = await requester.Client.GetEventAsync(roomId, eventId, Ct);
            Assert.Equal(DecryptionStatus.MissingKey, (await requester.Machine.DecryptRoomEventAsync(roomId, ev, Ct)).Status);

            // Act: sync both until the key arrives
            RoomEventDecryption? result = null;
            for (int round = 0; round < 8 && result?.Status != DecryptionStatus.Decrypted; round++)
            {
                await requester.SyncAsync();
                await sender.SyncAsync();
                result = await requester.Machine.DecryptRoomEventAsync(roomId, ev, Ct);
            }

            // Assert
            Assert.Equal(DecryptionStatus.Decrypted, result?.Status);
            Assert.True(result!.Event.TryGetContent(out MessageContent? message));
            Assert.Equal(secret, message.Body);
        }
        finally
        {
            await RemoveRoomAsync(sender.Client, roomId);
        }
    }

    /// <summary>Two users verify with emoji; the verifier's user-signing key signs the other's identity, so the verifier's other devices trust the other user's devices too.</summary>
    [Fact]
    public async Task CanCrossSignOtherUser()
    {
        // Arrange: recovered devices of two users
        (string alice, string password) = Account(1);
        (string bob, _) = Account(2);
        using HttpClient http = NewHttp();
        await using LiveDevice aliceDevice = await LiveDevice.LoginAsync(http, alice, password, "verifier");
        string aliceKey = await EnsureRecoveryAsync(aliceDevice, password);
        await using LiveDevice bobDevice = await LiveDevice.LoginAsync(http, bob, password, "verified");
        await EnsureRecoveryAsync(bobDevice, password);
        Verification? incoming = null;
        bobDevice.Machine.VerificationChanged += v => incoming ??= v;

        // Act: request the one device, accept, compare, confirm
        Verification outgoing = await aliceDevice.Machine.RequestVerificationAsync(bob, bobDevice.Client.DeviceId, Ct);
        for (int round = 0; round < 6 && incoming is null; round++) await bobDevice.SyncAsync();
        Assert.NotNull(incoming);
        await bobDevice.Machine.AcceptVerificationAsync(incoming, Ct);
        for (int round = 0; round < 8 && (outgoing.State != VerificationState.ShowingSas || incoming.State != VerificationState.ShowingSas); round++) await SyncAllAsync(aliceDevice, bobDevice);
        Assert.Equal(outgoing.Emoji, incoming.Emoji);
        await aliceDevice.Machine.ConfirmVerificationAsync(outgoing, Ct);
        await bobDevice.Machine.ConfirmVerificationAsync(incoming, Ct);
        for (int round = 0; round < 8 && (outgoing.State != VerificationState.Done || incoming.State != VerificationState.Done); round++) await SyncAllAsync(aliceDevice, bobDevice);

        // Assert both done, and a fresh device of the verifier trusts the other user through cross-signing
        Assert.Equal(VerificationState.Done, outgoing.State);
        Assert.Equal(VerificationState.Done, incoming.State);
        await using LiveDevice aliceOther = await LiveDevice.LoginAsync(http, alice, password, "second verifier");
        await aliceOther.Machine.RecoverAsync(aliceKey, Ct);
        await aliceOther.Machine.TrackUsersAsync([bob], Ct);
        Assert.True(aliceOther.Machine.IsIdentityTrusted(aliceOther.Machine.GetIdentity(bob)!));
        DeviceInfo verified = aliceOther.Machine.GetDevices(bob).Single(d => d.DeviceId == bobDevice.Client.DeviceId);
        Assert.Equal(DeviceTrust.CrossSigned, aliceOther.Machine.GetDeviceTrust(verified));
    }

    /// <summary>Return the test account at <paramref name="index"/> in testclient.txt and the password.</summary>
    private static (string UserId, string Password) Account(int index)
    {
        // Listed accounts
        (IReadOnlyList<string> userIds, string password) = LiveAccounts.Load();
        Assert.SkipWhen(userIds.Count <= index, $"testclient.txt lists no account number {index + 1}.");
        // Return account
        return (userIds[index], password);
    }

    /// <summary>Set up recovery on first use and save the key, or recovers with the saved key; return the recovery key.</summary>
    private static async Task<string> EnsureRecoveryAsync(LiveDevice device, string password)
    {
        // New account: set up
        string userId = device.Client.UserId!;
        SecurityStatus status = await device.Machine.GetSecurityStatusAsync(Ct);
        if (!status.CrossSigningSetUp)
        {
            string created = await device.Machine.SetUpRecoveryAsync(password, Ct);
            LiveAccounts.SaveRecoveryKey(userId, created);
            return created;
        }

        // Set up before: recover with the saved key
        string recoveryKey = LiveAccounts.GetRecoveryKey(userId) ?? throw new InvalidOperationException($"{userId} has cross-signing, but its recovery key is not in testclient-recovery.txt.");
        await device.Machine.RecoverAsync(recoveryKey, Ct);
        // Return key
        return recoveryKey;
    }

    /// <summary>Create a private encrypted room for one test.</summary>
    private static Task<string> CreateEncryptedRoomAsync(MatrixClient client)
    {
        // Encrypted from the start
        InitialStateEvent encryption = new() { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } };
        // Return room ID
        return client.CreateRoomAsync(new CreateRoomRequest { Name = "Uniform.Matrix.Protocol.Crypto.Tests security", Preset = RoomPresets.PrivateChat, InitialState = [encryption] }, Ct);
    }

    /// <summary>Leave and forget a room this test created.</summary>
    private static async Task RemoveRoomAsync(MatrixClient client, string? roomId)
    {
        // Only the created room
        if (roomId is null) return;
        await client.LeaveRoomAsync(roomId, cancellationToken: Ct);
        await client.ForgetRoomAsync(roomId, Ct);
    }

    /// <summary>Sync each device once.</summary>
    private static async Task SyncAllAsync(params LiveDevice[] devices)
    {
        // In order
        foreach (LiveDevice device in devices) await device.SyncAsync();
    }

    /// <summary>HTTP client for the live server.</summary>
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Text message content.</summary>
    private static MessageContent Text(string body) => new() { MessageType = MessageTypes.Text, Body = body };

    /// <summary>A logged-in test device with an in-memory <see cref="OlmMachine"/>; disposing logs it out.</summary>
    private sealed class LiveDevice : IAsyncDisposable
    {
        private string? since;

        /// <summary>Wrap a client and its machine.</summary>
        private LiveDevice(MatrixClient client, OlmMachine machine)
        {
            // Store parts
            Client = client;
            Machine = machine;
        }

        /// <summary>Logged-in client.</summary>
        public MatrixClient Client { get; }
        /// <summary>Encryption of the device.</summary>
        public OlmMachine Machine { get; }

        /// <summary>Log in a new device, uploads its keys and take a sync position.</summary>
        public static async Task<LiveDevice> LoginAsync(HttpClient http, string userId, string password, string name)
        {
            // Log in and start encryption
            MatrixClient client = await LiveAccounts.LoginAsync(http, userId, password, $"Uniform.Matrix.Protocol.Crypto.Tests {name}", Ct);
            OlmMachine machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), new InMemoryCryptoStore(), userId, client.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: Ct);
            await machine.UploadInitialKeysAsync(Ct);
            LiveDevice device = new(client, machine);
            device.since = (await client.SyncAsync(filter: Filter, cancellationToken: Ct)).NextBatch;
            // Return device
            return device;
        }

        /// <summary>Sync once with a short wait and feeds the result to the machine.</summary>
        public async Task SyncAsync()
        {
            // Incremental sync
            SyncResponse sync = await Client.SyncAsync(since, TimeSpan.FromSeconds(1), Filter, cancellationToken: Ct);
            since = sync.NextBatch;
            await Machine.ReceiveSyncAsync(sync, Ct);
        }

        /// <summary>Log out and free the machine.</summary>
        public async ValueTask DisposeAsync()
        {
            // Log out the test device
            await Client.LogoutAsync(Ct);
            Machine.Dispose();
        }
    }
}
