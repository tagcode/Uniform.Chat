namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

/// <summary>Protocol tests of cross-signing, secret storage, key backup, key requests and SAS verification through <see cref="FakeKeyServer"/>.</summary>
public class SecurityTests
{
    private const string RoomId = "!secret:hs.example";
    private const string Alice = "@alice:hs.example";
    private const string Bob = "@bob:hs.example";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeKeyServer server = new();

    /// <summary>Create a device that has uploaded its keys.</summary>
    private async Task<OlmMachine> CreateDeviceAsync(string user, string device)
    {
        // Create and upload
        OlmMachine machine = await OlmMachine.CreateAsync(server.For(user, device), new InMemoryCryptoStore(), user, device, RandomNumberGenerator.GetBytes(32), cancellationToken: Ct);
        await machine.UploadInitialKeysAsync(Ct);
        // Return device
        return machine;
    }

    /// <summary>Deliver to-device messages until every inbox is empty, like several syncs in a row.</summary>
    private async Task PumpAsync(params OlmMachine[] machines)
    {
        // A few rounds are enough for any exchange here
        for (int round = 0; round < 10; round++)
            foreach (OlmMachine machine in machines) await machine.ReceiveSyncAsync(server.Sync(machine.UserId, machine.DeviceId), Ct);
    }

    /// <summary>Wrap encrypted content as a timeline event.</summary>
    private static ClientEvent AsEvent(EncryptedContent content, string sender, string eventId) => new()
    {
        Type = EventTypes.RoomEncrypted,
        Sender = sender,
        EventId = eventId,
        OriginServerTs = 1,
        Content = JsonSerializer.SerializeToElement(content, MatrixJson.Options),
    };

    /// <summary>Text message content.</summary>
    private static MessageContent Text(string body) => new() { MessageType = MessageTypes.Text, Body = body };

    /// <summary>Setting up recovery publishes cross-signing, signs this device, creates a backup and stores the secrets in secret storage.</summary>
    [Fact]
    public async Task CanSetUpRecovery()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");

        // Act
        string recoveryKey = await alice.SetUpRecoveryAsync(password: null, Ct);
        SecurityStatus status = await alice.GetSecurityStatusAsync(Ct);

        // Assert
        Assert.StartsWith("Es", recoveryKey);
        Assert.True(status.CrossSigningSetUp && status.IdentityTrusted && status.HasPrivateKeys && status.ThisDeviceCrossSigned);
        Assert.True(status.BackupUsable && status.SecretStorageSetUp);
        Assert.NotNull(server.GetAccountData(Alice, SecretNames.CrossSigningMaster)?["encrypted"]);
        Assert.NotNull(server.GetAccountData(Alice, SecretNames.MegolmBackup)?["encrypted"]);
    }

    /// <summary>Existing cross-signing keys are never replaced, since that would distrust every device.</summary>
    [Fact]
    public async Task CanRefuseToReplaceCrossSigning()
    {
        // Arrange
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");
        await alice1.SetUpRecoveryAsync(password: null, Ct);

        // Act and assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice2.SetUpRecoveryAsync(password: null, Ct));
        Assert.Equal(1, server.CrossSigningUploads);
    }

    /// <summary>A new login recovers with the recovery key: it trusts the identity, is cross-signed, and reads old messages from the backup.</summary>
    [Fact]
    public async Task CanRecoverOnNewDevice()
    {
        // Arrange: a message only the first device has the key for, backed up
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        string recoveryKey = await alice1.SetUpRecoveryAsync(password: null, Ct);
        EncryptedContent content = await alice1.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("before you came"), [], cancellationToken: Ct);
        Assert.Equal(1, await alice1.BackUpRoomKeysAsync(Ct));
        Assert.Equal(0, await alice1.BackUpRoomKeysAsync(Ct));

        // Act: new device, typing the key with extra spaces
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");
        List<string> announced = [];
        alice2.RoomKeyReceived += (room, session) => announced.Add(session);
        RecoveryResult result = await alice2.RecoverAsync($"  {recoveryKey}  ", Ct);

        // Assert keys, trust and the old message
        Assert.True(result.CrossSigningRestored && result.BackupRestored);
        Assert.Equal(1, result.KeysImported);
        Assert.Equal([content.SessionId!], announced);
        RoomEventDecryption decrypted = await alice2.DecryptRoomEventAsync(RoomId, AsEvent(content, Alice, "$1"), Ct);
        Assert.Equal(DecryptionStatus.Decrypted, decrypted.Status);
        Assert.True((await alice2.GetSecurityStatusAsync(Ct)).ThisDeviceCrossSigned);
        await alice1.TrackUsersAsync([Alice], Ct);
        await alice2.GetSecurityStatusAsync(Ct);
        Assert.Equal(DeviceTrust.CrossSigned, alice2.GetDeviceTrust(alice2.GetDevices(Alice).Single(d => d.DeviceId == "A1")));
    }

    /// <summary>A wrong recovery key is rejected and nothing is restored.</summary>
    [Fact]
    public async Task CanRejectWrongRecoveryKey()
    {
        // Arrange
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        await alice1.SetUpRecoveryAsync(password: null, Ct);
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");

        // Act and assert
        string wrong = SecretStorageCrypto.EncodeRecoveryKey(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<CryptographicException>(() => alice2.RecoverAsync(wrong, Ct));
        Assert.False((await alice2.GetSecurityStatusAsync(Ct)).HasPrivateKeys);
    }

    /// <summary>A missing room key is requested from our other devices; only a cross-signed device gets an answer.</summary>
    [Fact]
    public async Task CanForwardKeyToTrustedOwnDevice()
    {
        // Arrange: A1 and A2 cross-signed, A3 not; the key stays on A1
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        string recoveryKey = await alice1.SetUpRecoveryAsync(password: null, Ct);
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");
        await alice2.RecoverAsync(recoveryKey, Ct);
        using OlmMachine alice3 = await CreateDeviceAsync(Alice, "A3");
        await alice1.TrackUsersAsync([Alice], Ct);
        EncryptedContent content = await alice1.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("ask me"), [], cancellationToken: Ct);
        await alice1.GetSecurityStatusAsync(Ct);
        ClientEvent ev = AsEvent(content, Alice, "$1");

        // Act: both fail, request, and sync
        Assert.Equal(DecryptionStatus.MissingKey, (await alice3.DecryptRoomEventAsync(RoomId, ev, Ct)).Status);
        Assert.Equal(DecryptionStatus.MissingKey, (await alice2.DecryptRoomEventAsync(RoomId, ev, Ct)).Status);
        await PumpAsync(alice1, alice2, alice3);

        // Assert
        Assert.Equal(DecryptionStatus.Decrypted, (await alice2.DecryptRoomEventAsync(RoomId, ev, Ct)).Status);
        Assert.Equal(DecryptionStatus.MissingKey, (await alice3.DecryptRoomEventAsync(RoomId, ev, Ct)).Status);
    }

    /// <summary>Two users compare the same emoji, confirm, and each marks the other's device verified.</summary>
    [Fact]
    public async Task CanVerifyOtherUserWithEmoji()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        Verification? incoming = null;
        bob.VerificationChanged += v => incoming ??= v;

        // Act: request, accept, exchange keys
        Verification outgoing = await alice.RequestVerificationAsync(Bob, cancellationToken: Ct);
        await PumpAsync(bob);
        Assert.NotNull(incoming);
        await bob.AcceptVerificationAsync(incoming, Ct);
        await PumpAsync(alice, bob);

        // Assert same emoji, then confirm both
        Assert.Equal(VerificationState.ShowingSas, outgoing.State);
        Assert.Equal(VerificationState.ShowingSas, incoming.State);
        Assert.Equal(7, outgoing.Emoji.Count);
        Assert.Equal(outgoing.Emoji, incoming.Emoji);
        Assert.Equal(outgoing.Decimals, incoming.Decimals);
        await alice.ConfirmVerificationAsync(outgoing, Ct);
        await bob.ConfirmVerificationAsync(incoming, Ct);
        await PumpAsync(alice, bob);
        Assert.Equal(VerificationState.Done, outgoing.State);
        Assert.Equal(VerificationState.Done, incoming.State);
        Assert.Equal(DeviceTrust.Verified, alice.GetDeviceTrust(alice.GetDevices(Bob).Single()));
        Assert.Equal(DeviceTrust.Verified, bob.GetDeviceTrust(bob.GetDevices(Alice).Single()));
    }

    /// <summary>Verifying a new own device with emoji gives it the cross-signing and backup keys, so it needs no recovery key.</summary>
    [Fact]
    public async Task CanShareSecretsAfterVerifyingOwnDevice()
    {
        // Arrange
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        await alice1.SetUpRecoveryAsync(password: null, Ct);
        await alice1.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("old"), [], cancellationToken: Ct);
        await alice1.BackUpRoomKeysAsync(Ct);
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");
        Verification? incoming = null;
        alice1.VerificationChanged += v => incoming ??= v;
        List<string> secrets = [];
        alice2.SecretReceived += secrets.Add;

        // Act: new device asks, old device accepts, both confirm
        Verification outgoing = await alice2.RequestVerificationAsync(Alice, cancellationToken: Ct);
        await PumpAsync(alice1);
        await alice1.AcceptVerificationAsync(incoming!, Ct);
        await PumpAsync(alice2, alice1);
        await alice2.ConfirmVerificationAsync(outgoing, Ct);
        await alice1.ConfirmVerificationAsync(incoming!, Ct);
        await PumpAsync(alice2, alice1);

        // Assert
        Assert.Equal(VerificationState.Done, outgoing.State);
        Assert.Equal(4, secrets.Count);
        SecurityStatus status = await alice2.GetSecurityStatusAsync(Ct);
        Assert.True(status.HasPrivateKeys && status.IdentityTrusted && status.ThisDeviceCrossSigned && status.BackupUsable);
    }

    /// <summary>Mismatching emoji cancel the verification on both sides, and nothing is verified.</summary>
    [Fact]
    public async Task CanCancelOnMismatch()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        Verification? incoming = null;
        bob.VerificationChanged += v => incoming ??= v;
        Verification outgoing = await alice.RequestVerificationAsync(Bob, cancellationToken: Ct);
        await PumpAsync(bob);
        await bob.AcceptVerificationAsync(incoming!, Ct);
        await PumpAsync(alice, bob);

        // Act
        await bob.CancelVerificationAsync(incoming!, "m.mismatched_sas", Ct);
        await PumpAsync(alice);

        // Assert
        Assert.Equal(VerificationState.Cancelled, outgoing.State);
        Assert.Equal("m.mismatched_sas", outgoing.CancelCode);
        Assert.Equal(DeviceTrust.Unverified, alice.GetDeviceTrust(alice.GetDevices(Bob).Single()));
    }
}
