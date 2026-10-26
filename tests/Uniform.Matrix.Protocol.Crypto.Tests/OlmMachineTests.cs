namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Protocol tests of <see cref="OlmMachine"/>: several devices talking through <see cref="FakeKeyServer"/>.</summary>
public class OlmMachineTests
{
    private const string RoomId = "!secret:hs.example";
    private const string Alice = "@alice:hs.example";
    private const string Bob = "@bob:hs.example";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeKeyServer server = new();

    /// <summary>Create a device that has uploaded its keys.</summary>
    private async Task<OlmMachine> CreateDeviceAsync(string user, string device, ICryptoStore? store = null, byte[]? pickleKey = null)
    {
        // Create and upload
        OlmMachine machine = await OlmMachine.CreateAsync(server.For(user, device), store ?? new InMemoryCryptoStore(), user, device, pickleKey ?? RandomNumberGenerator.GetBytes(32), cancellationToken: Ct);
        await machine.UploadInitialKeysAsync(Ct);
        // Return device
        return machine;
    }

    /// <summary>Wrap encrypted content as a timeline event from <paramref name="sender"/>.</summary>
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

    /// <summary>Return the body of a decrypted message.</summary>
    private static string? Body(RoomEventDecryption result) => result.Event.TryGetContent(out MessageContent? m) ? m.Body : null;

    /// <summary>First upload publishes self-signed device keys, half the one-time key capacity and a fallback key.</summary>
    [Fact]
    public async Task CanUploadSignedKeys()
    {
        // Act
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");

        // Assert device keys are self-signed
        JsonObject keys = server.GetDeviceKeys(Alice, "A1")!;
        string signature = CanonicalJson.GetSignature(keys, Alice, "ed25519:A1")!;
        Assert.True(Ed25519.Verify(alice.IdentityKeys.Ed25519, CanonicalJson.SigningForm(keys), signature));
        Assert.Equal(alice.IdentityKeys.Curve25519, keys["keys"]!["curve25519:A1"]!.GetValue<string>());

        // Assert one-time keys
        Assert.InRange(server.OneTimeKeyCount(Alice, "A1"), 25, 100);
    }

    /// <summary>A message reaches every other device of every member, including the sender's own other device and the sender itself.</summary>
    [Fact]
    public async Task CanEncryptForAllDevices()
    {
        // Arrange: Alice on two devices, Bob on two
        using OlmMachine alice1 = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine alice2 = await CreateDeviceAsync(Alice, "A2");
        using OlmMachine bob1 = await CreateDeviceAsync(Bob, "B1");
        using OlmMachine bob2 = await CreateDeviceAsync(Bob, "B2");

        // Act
        EncryptedContent content = await alice1.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("hello all"), [Alice, Bob], cancellationToken: Ct);
        ClientEvent ev = AsEvent(content, Alice, "$1");

        // Assert each device decrypts after its sync
        foreach ((OlmMachine machine, string user, string device) in new[] { (alice2, Alice, "A2"), (bob1, Bob, "B1"), (bob2, Bob, "B2") })
        {
            await machine.ReceiveSyncAsync(server.Sync(user, device), Ct);
            RoomEventDecryption result = await machine.DecryptRoomEventAsync(RoomId, ev, Ct);
            Assert.Equal(DecryptionStatus.Decrypted, result.Status);
            Assert.Equal("hello all", Body(result));
            Assert.Equal(EventTypes.RoomMessage, result.Event.Type);
        }

        // Assert the sender reads its own message, and knows the sender device after tracking
        await bob1.TrackUsersAsync([Alice], Ct);
        Assert.Equal("hello all", Body(await alice1.DecryptRoomEventAsync(RoomId, ev, Ct)));
        Assert.Equal("A1", (await bob1.DecryptRoomEventAsync(RoomId, ev, Ct)).SenderDevice?.DeviceId);
    }

    /// <summary>Later messages reuse the session without resending keys; Olm sessions continue with normal messages.</summary>
    [Fact]
    public async Task CanReuseSessionForLaterMessages()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        EncryptedContent first = await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("one"), [Alice, Bob], cancellationToken: Ct);
        await bob.ReceiveSyncAsync(server.Sync(Bob, "B1"), Ct);

        // Act
        EncryptedContent second = await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("two"), [Alice, Bob], cancellationToken: Ct);

        // Assert same session, no new key delivery, both decrypt
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Empty(server.Sync(Bob, "B1").ToDevice.Events);
        Assert.Equal("one", Body(await bob.DecryptRoomEventAsync(RoomId, AsEvent(first, Alice, "$1"), Ct)));
        Assert.Equal("two", Body(await bob.DecryptRoomEventAsync(RoomId, AsEvent(second, Alice, "$2"), Ct)));
    }

    /// <summary>Without the key the event waits; when the key arrives, <see cref="OlmMachine.RoomKeyReceived"/> fires and it decrypts.</summary>
    [Fact]
    public async Task CanDecryptAfterKeyArrives()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        ClientEvent ev = AsEvent(await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("late"), [Alice, Bob], cancellationToken: Ct), Alice, "$1");
        List<(string, string)> received = [];
        bob.RoomKeyReceived += (room, session) => received.Add((room, session));

        // Act and assert: before sync
        RoomEventDecryption before = await bob.DecryptRoomEventAsync(RoomId, ev, Ct);
        Assert.Equal(DecryptionStatus.MissingKey, before.Status);

        // Act and assert: after sync
        await bob.ReceiveSyncAsync(server.Sync(Bob, "B1"), Ct);
        Assert.Equal((RoomId, before.SessionId!), Assert.Single(received));
        Assert.Equal("late", Body(await bob.DecryptRoomEventAsync(RoomId, ev, Ct)));
    }

    /// <summary>A key is bound to its room, and a ciphertext cannot be replayed as another event.</summary>
    [Fact]
    public async Task CanRejectOtherRoomAndReplay()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        EncryptedContent content = await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("once"), [Alice, Bob], cancellationToken: Ct);
        await bob.ReceiveSyncAsync(server.Sync(Bob, "B1"), Ct);
        Assert.Equal(DecryptionStatus.Decrypted, (await bob.DecryptRoomEventAsync(RoomId, AsEvent(content, Alice, "$1"), Ct)).Status);

        // Act and assert
        Assert.Equal(DecryptionStatus.MissingKey, (await bob.DecryptRoomEventAsync("!other:hs.example", AsEvent(content, Alice, "$1"), Ct)).Status);
        Assert.Equal(DecryptionStatus.Failed, (await bob.DecryptRoomEventAsync(RoomId, AsEvent(content, Alice, "$copy"), Ct)).Status);
        Assert.Equal(DecryptionStatus.Decrypted, (await bob.DecryptRoomEventAsync(RoomId, AsEvent(content, Alice, "$1"), Ct)).Status);
    }

    /// <summary>A new session starts when a member leaves or the message limit is reached, so leavers cannot read on.</summary>
    [Fact]
    public async Task CanRotateSessions()
    {
        // Arrange
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        EncryptionContent settings = new() { Algorithm = EncryptionAlgorithms.Megolm, RotationPeriodMsgs = 2 };

        // Act
        string? first = (await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("1"), [Alice, Bob], settings, Ct)).SessionId;
        string? same = (await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("2"), [Alice, Bob], settings, Ct)).SessionId;
        string? byCount = (await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("3"), [Alice, Bob], settings, Ct)).SessionId;
        string? byLeave = (await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("4"), [Alice], settings, Ct)).SessionId;

        // Assert
        Assert.Equal(first, same);
        Assert.NotEqual(first, byCount);
        Assert.NotEqual(byCount, byLeave);
    }

    /// <summary>Device keys that fail their self-signature are ignored.</summary>
    [Fact]
    public async Task CanIgnoreTamperedDeviceKeys()
    {
        // Arrange: Bob's uploaded curve key is replaced by the server
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        using OlmMachine mallory = await CreateDeviceAsync("@mallory:hs.example", "M1");
        server.GetDeviceKeys(Bob, "B1")!["keys"]!["curve25519:B1"] = mallory.IdentityKeys.Curve25519;

        // Act
        await alice.TrackUsersAsync([Bob], Ct);

        // Assert
        Assert.Empty(alice.GetDevices(Bob));
    }

    /// <summary>A device restored from its store keeps its identity and decrypts with stored keys.</summary>
    [Fact]
    public async Task CanRestoreFromStore()
    {
        // Arrange: Bob receives a key, then restarts
        InMemoryCryptoStore bobStore = new();
        byte[] pickleKey = RandomNumberGenerator.GetBytes(32);
        using OlmMachine alice = await CreateDeviceAsync(Alice, "A1");
        OlmMachine bob = await CreateDeviceAsync(Bob, "B1", bobStore, pickleKey);
        IdentityKeys identity = bob.IdentityKeys;
        ClientEvent ev = AsEvent(await alice.EncryptRoomEventAsync(RoomId, EventTypes.RoomMessage, Text("persisted"), [Alice, Bob], cancellationToken: Ct), Alice, "$1");
        await bob.ReceiveSyncAsync(server.Sync(Bob, "B1"), Ct);
        bob.Dispose();
        int keysBefore = server.OneTimeKeyCount(Bob, "B1");

        // Act
        using OlmMachine restored = await OlmMachine.CreateAsync(server.For(Bob, "B1"), bobStore, Bob, "B1", pickleKey, cancellationToken: Ct);
        await restored.UploadInitialKeysAsync(Ct);

        // Assert same identity, no second device key upload, stored room key works
        Assert.Equal(identity, restored.IdentityKeys);
        Assert.Equal(keysBefore, server.OneTimeKeyCount(Bob, "B1"));
        Assert.Equal("persisted", Body(await restored.DecryptRoomEventAsync(RoomId, ev, Ct)));
    }

    /// <summary>Claimed one-time keys are replaced when sync reports the server running low.</summary>
    [Fact]
    public async Task CanTopUpOneTimeKeys()
    {
        // Arrange: many devices claim Bob's keys
        using OlmMachine bob = await CreateDeviceAsync(Bob, "B1");
        int full = server.OneTimeKeyCount(Bob, "B1");
        for (int i = 0; i < 5; i++)
        {
            using OlmMachine other = await CreateDeviceAsync($"@u{i}:hs.example", "D");
            await other.EncryptRoomEventAsync($"!r{i}:hs.example", EventTypes.RoomMessage, Text("hi"), [$"@u{i}:hs.example", Bob], cancellationToken: Ct);
        }
        Assert.Equal(full - 5, server.OneTimeKeyCount(Bob, "B1"));

        // Act
        await bob.ReceiveSyncAsync(server.Sync(Bob, "B1"), Ct);

        // Assert
        Assert.Equal(full, server.OneTimeKeyCount(Bob, "B1"));
    }
}
