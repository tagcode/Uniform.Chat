namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using Uniform.Matrix.Client;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>End-to-end encryption against the real test homeserver: two devices of the test account in a new encrypted room; exclude with <c>dotnet test -- --filter-not-trait "Category=Live"</c>.</summary>
/// <remarks>Shares one room key with all devices of the account, and removes only the room it created.</remarks>
[Trait("Category", "Live")]
public class LiveEncryptionTests
{
    /// <summary>Device A encrypts; device B receives the key over Olm via the server and decrypts the Megolm message.</summary>
    [Fact]
    public async Task CanExchangeEncryptedMessageBetweenDevices()
    {
        // Arrange: two logged-in devices
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string userId, string password) = LoadAccount();
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, UserId.Parse(userId).ServerName, ct);
        MatrixClient deviceA = new(http, homeserver);
        MatrixClient deviceB = new(http, homeserver);
        await deviceA.LoginWithPasswordAsync(userId, password, "Uniform.Matrix.Protocol.Crypto.Tests A", ct);
        await deviceB.LoginWithPasswordAsync(userId, password, "Uniform.Matrix.Protocol.Crypto.Tests B", ct);
        using OlmMachine machineA = await OlmMachine.CreateAsync(new MatrixKeyTransport(deviceA), new InMemoryCryptoStore(), userId, deviceA.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: ct);
        using OlmMachine machineB = await OlmMachine.CreateAsync(new MatrixKeyTransport(deviceB), new InMemoryCryptoStore(), userId, deviceB.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: ct);
        string? roomId = null;
        try
        {
            // Publish keys; B syncs once to get a position
            await machineA.UploadInitialKeysAsync(ct);
            await machineB.UploadInitialKeysAsync(ct);
            string filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 1 } } }.ToJson();
            string since = (await deviceB.SyncAsync(filter: filter, cancellationToken: ct)).NextBatch;

            // A creates an encrypted room and sends an encrypted message
            InitialStateEvent encryption = new() { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } };
            roomId = await deviceA.CreateRoomAsync(new CreateRoomRequest { Name = "Uniform.Matrix.Protocol.Crypto.Tests", Preset = RoomPresets.PrivateChat, InitialState = [encryption] }, ct);
            string secret = $"secret {Guid.NewGuid():N}";
            EncryptedContent content = await machineA.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = secret }, [userId], cancellationToken: ct);
            string eventId = await deviceA.SendEventAsync(roomId, EventTypes.RoomEncrypted, content, cancellationToken: ct);

            // B syncs until the event arrives, feeding every sync to its machine
            RoomEventDecryption? result = null;
            for (int attempt = 0; attempt < 10 && result?.Status != DecryptionStatus.Decrypted; attempt++)
            {
                SyncResponse sync = await deviceB.SyncAsync(since, TimeSpan.FromSeconds(5), cancellationToken: ct);
                since = sync.NextBatch;
                await machineB.ReceiveSyncAsync(sync, ct);
                ClientEvent? encrypted = sync.Rooms.Join.GetValueOrDefault(roomId)?.Timeline.Events.FirstOrDefault(e => e.EventId == eventId);
                if (encrypted is not null) result = await machineB.DecryptRoomEventAsync(roomId, encrypted, ct);
            }

            // Assert B read it, and the server only ever saw ciphertext
            Assert.NotNull(result);
            Assert.Equal(DecryptionStatus.Decrypted, result.Status);
            Assert.True(result.Event.TryGetContent(out MessageContent? message));
            Assert.Equal(secret, message.Body);
            ClientEvent stored = await deviceA.GetEventAsync(roomId, eventId, ct);
            Assert.Equal(EventTypes.RoomEncrypted, stored.Type);
            Assert.DoesNotContain(secret, stored.Content.GetRawText());
        }
        finally
        {
            // Remove only this room, and log out both test devices
            if (roomId is not null) await deviceA.LeaveRoomAsync(roomId, cancellationToken: ct);
            if (roomId is not null) await deviceA.ForgetRoomAsync(roomId, ct);
            await deviceA.LogoutAsync(ct);
            await deviceB.LogoutAsync(ct);
        }
    }

    /// <summary>An encrypted attachment survives the media repository, which stores only ciphertext; also reports the account's security state, reading only.</summary>
    [Fact]
    public async Task CanRoundTripEncryptedAttachment()
    {
        // Arrange: one logged-in device
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string userId, string password) = LoadAccount();
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, UserId.Parse(userId).ServerName, ct);
        MatrixClient client = new(http, homeserver);
        await client.LoginWithPasswordAsync(userId, password, "Uniform.Matrix.Protocol.Crypto.Tests attachment", ct);
        try
        {
            // Act: upload encrypted, download raw and decrypted
            byte[] file = RandomNumberGenerator.GetBytes(3000);
            EncryptedFile encrypted = await client.UploadEncryptedAsync(file, ct);
            using MediaDownload raw = await client.DownloadMediaAsync(MxcUri.Parse(encrypted.Url!), ct);
            using MemoryStream stored = new();
            await raw.Content.CopyToAsync(stored, ct);
            byte[] decrypted = await client.DownloadEncryptedAsync(encrypted, ct);

            // Assert
            Assert.Equal(file, decrypted);
            Assert.NotEqual(file, stored.ToArray());

            // Report cross-signing, backup and secret storage without changing them
            using OlmMachine machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), new InMemoryCryptoStore(), userId, client.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: ct);
            SecurityStatus status = await machine.GetSecurityStatusAsync(ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"Security of {userId}: {status}");
        }
        finally
        {
            // Log out the test device
            await client.LogoutAsync(ct);
        }
    }

    /// <summary>Two test devices verify each other with SAS over the server's to-device messages; the request names the device, so the account's other sessions are not asked.</summary>
    [Fact]
    public async Task CanVerifyTestDevicesWithEmoji()
    {
        // Arrange: two logged-in devices with keys
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string userId, string password) = LoadAccount();
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, UserId.Parse(userId).ServerName, ct);
        MatrixClient deviceA = new(http, homeserver);
        MatrixClient deviceB = new(http, homeserver);
        await deviceA.LoginWithPasswordAsync(userId, password, "Uniform.Matrix.Protocol.Crypto.Tests SAS A", ct);
        await deviceB.LoginWithPasswordAsync(userId, password, "Uniform.Matrix.Protocol.Crypto.Tests SAS B", ct);
        using OlmMachine machineA = await OlmMachine.CreateAsync(new MatrixKeyTransport(deviceA), new InMemoryCryptoStore(), userId, deviceA.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: ct);
        using OlmMachine machineB = await OlmMachine.CreateAsync(new MatrixKeyTransport(deviceB), new InMemoryCryptoStore(), userId, deviceB.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: ct);
        try
        {
            await machineA.UploadInitialKeysAsync(ct);
            await machineB.UploadInitialKeysAsync(ct);
            string filter = new Filter { Room = new RoomFilter { Rooms = [] } }.ToJson();
            string? sinceA = (await deviceA.SyncAsync(filter: filter, cancellationToken: ct)).NextBatch;
            string? sinceB = (await deviceB.SyncAsync(filter: filter, cancellationToken: ct)).NextBatch;
            Verification? incoming = null;
            machineB.VerificationChanged += v => incoming ??= v;

            // Syncs both devices a few times
            async Task PumpAsync(int rounds)
            {
                for (int i = 0; i < rounds; i++)
                {
                    SyncResponse a = await deviceA.SyncAsync(sinceA, TimeSpan.FromSeconds(1), filter, cancellationToken: ct);
                    sinceA = a.NextBatch;
                    await machineA.ReceiveSyncAsync(a, ct);
                    SyncResponse b = await deviceB.SyncAsync(sinceB, TimeSpan.FromSeconds(1), filter, cancellationToken: ct);
                    sinceB = b.NextBatch;
                    await machineB.ReceiveSyncAsync(b, ct);
                }
            }

            // Act: request B by device ID, accept, compare, confirm
            Verification outgoing = await machineA.RequestVerificationAsync(userId, deviceB.DeviceId, ct);
            for (int i = 0; i < 5 && incoming is null; i++) await PumpAsync(1);
            Assert.NotNull(incoming);
            await machineB.AcceptVerificationAsync(incoming, ct);
            for (int i = 0; i < 8 && outgoing.State != VerificationState.ShowingSas; i++) await PumpAsync(1);
            await PumpAsync(1);
            Assert.Equal(outgoing.Emoji, incoming.Emoji);
            await machineA.ConfirmVerificationAsync(outgoing, ct);
            await machineB.ConfirmVerificationAsync(incoming, ct);
            for (int i = 0; i < 8 && (outgoing.State != VerificationState.Done || incoming.State != VerificationState.Done); i++) await PumpAsync(1);

            // Assert
            Assert.Equal(VerificationState.Done, outgoing.State);
            Assert.Equal(VerificationState.Done, incoming.State);
            Assert.Equal(DeviceTrust.Verified, machineA.GetDeviceTrust(machineA.GetDevices(userId).Single(d => d.DeviceId == deviceB.DeviceId)));
        }
        finally
        {
            // Log out both test devices
            await deviceA.LogoutAsync(ct);
            await deviceB.LogoutAsync(ct);
        }
    }

    /// <summary>Return the first test account and the password.</summary>
    private static (string UserId, string Password) LoadAccount()
    {
        // First listed account
        (IReadOnlyList<string> userIds, string password) = LiveAccounts.Load();
        // Return account
        return (userIds[0], password);
    }
}
