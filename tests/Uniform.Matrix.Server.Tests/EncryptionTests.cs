namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Client;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>End-to-end encryption through the server with our own <see cref="OlmMachine"/>: keys, to-device messages, device lists, cross-signing, verification and key backup.</summary>
/// <remarks>These are the flows of the live encryption tests, against the in-memory server instead of a Synapse account.</remarks>
public class EncryptionTests
{
    private const string Password = "password";
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };
    private static readonly string Filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 5 } } }.ToJson();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A user's message in an encrypted room is read by another user's device; the server stores only ciphertext.</summary>
    [Fact]
    public async Task ExchangeEncryptedMessage()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device alice = await Device.RegisterAsync(server, "alice");
        using Device bob = await Device.RegisterAsync(server, "bob");
        string roomId = await CreateEncryptedRoomAsync(alice.Client, bob.Client);
        await bob.Client.JoinRoomAsync(roomId, cancellationToken: Ct);
        await bob.SyncAsync();

        // Act
        string secret = $"secret {Guid.NewGuid():N}";
        EncryptedContent content = await alice.Machine.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, Text(secret), [alice.UserId, bob.UserId], cancellationToken: Ct);
        string eventId = await alice.Client.SendEventAsync(roomId, EventTypes.RoomEncrypted, content, cancellationToken: Ct);
        RoomEventDecryption? result = null;
        for (int round = 0; round < 5 && result?.Status != DecryptionStatus.Decrypted; round++)
        {
            await bob.SyncAsync();
            result = await bob.Machine.DecryptRoomEventAsync(roomId, await bob.Client.GetEventAsync(roomId, eventId, Ct), Ct);
        }

        // Assert
        Assert.Equal(DecryptionStatus.Decrypted, result?.Status);
        Assert.True(result!.Event.TryGetContent(out MessageContent? message));
        Assert.Equal(secret, message.Body);
        ClientEvent stored = await alice.Client.GetEventAsync(roomId, eventId, Ct);
        Assert.Equal(EventTypes.RoomEncrypted, stored.Type);
        Assert.DoesNotContain(secret, stored.Content.GetRawText());
    }

    /// <summary>Claiming takes each one-time key once, then keeps giving the fallback key; syncs reports the counts and the used fallback.</summary>
    [Fact]
    public async Task ClaimKeys()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device alice = await Device.RegisterAsync(server, "alice");
        MatrixClient bob = await RegisterAsync(server, "bob");
        int before = (await alice.Client.SyncAsync(cancellationToken: Ct)).DeviceOneTimeKeysCount?.GetValueOrDefault(EncryptionAlgorithms.SignedCurve25519) ?? 0;
        Dictionary<string, IReadOnlyList<string>> request = new() { [alice.UserId] = [alice.Client.DeviceId!] };

        // Act: claim every one-time key, then once more
        HashSet<string> claimed = [];
        for (int i = 0; i < before; i++) claimed.UnionWith(KeyIds(await bob.ClaimKeysAsync(request, Ct), alice));
        List<string> fallback = KeyIds(await bob.ClaimKeysAsync(request, Ct), alice);
        List<string> fallbackAgain = KeyIds(await bob.ClaimKeysAsync(request, Ct), alice);
        SyncResponse after = await alice.Client.SyncAsync(cancellationToken: Ct);

        // Assert
        Assert.True(before > 0);
        Assert.Equal(before, claimed.Count);
        Assert.Equal(fallback, fallbackAgain);
        Assert.Single(fallback);
        Assert.Equal(0, after.DeviceOneTimeKeysCount?.GetValueOrDefault(EncryptionAlgorithms.SignedCurve25519));
        Assert.DoesNotContain(EncryptionAlgorithms.SignedCurve25519, after.DeviceUnusedFallbackKeyTypes ?? []);
    }

    /// <summary>To-device messages reach every device of the user once; a retried transaction is not delivered again.</summary>
    [Fact]
    public async Task ToDeviceMessages()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await RegisterAsync(server, "alice");
        MatrixClient bob = await RegisterAsync(server, "bob");
        MatrixClient bobOther = server.CreateClient();
        await bobOther.LoginWithPasswordAsync(bob.UserId!, Password, cancellationToken: Ct);
        string since = (await bob.SyncAsync(cancellationToken: Ct)).NextBatch;
        Dictionary<string, Dictionary<string, object>> messages = new() { [bob.UserId!] = new() { ["*"] = new Dictionary<string, string> { ["hello"] = "world" } } };

        // Act
        await alice.SendToDeviceAsync("org.example.ping", messages, "txn1", Ct);
        await alice.SendToDeviceAsync("org.example.ping", messages, "txn1", Ct);
        SyncResponse first = await bob.SyncAsync(since, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse repeated = await bob.SyncAsync(since, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse next = await bob.SyncAsync(first.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse other = await bobOther.SyncAsync(cancellationToken: Ct);

        // Assert: delivered until a later token proves it arrived
        ToDeviceEvent message = Assert.Single(first.ToDevice.Events);
        Assert.Equal("org.example.ping", message.Type);
        Assert.Equal(alice.UserId, message.Sender);
        Assert.Equal("world", message.Content.GetProperty("hello").GetString());
        Assert.Single(repeated.ToDevice.Events);
        Assert.Empty(next.ToDevice.Events);
        Assert.Single(other.ToDevice.Events);
    }

    /// <summary>Others see a user's device list change when a device is added or deleted, and see the user leave when no room is shared.</summary>
    [Fact]
    public async Task DeviceListChanges()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device alice = await Device.RegisterAsync(server, "alice");
        MatrixClient bob = await RegisterAsync(server, "bob");
        string roomId = await alice.Client.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        string start = (await bob.SyncAsync(cancellationToken: Ct)).NextBatch;

        // Act and assert: bob joins, so alice's devices are news to him
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        SyncResponse joined = await bob.SyncAsync(start, TimeSpan.Zero, cancellationToken: Ct);
        Assert.Contains(alice.UserId, joined.DeviceLists.Changed);

        // Act and assert: alice adds a device
        using Device aliceOther = await Device.LoginAsync(server, alice.UserId);
        SyncResponse added = await bob.SyncAsync(joined.NextBatch, TimeSpan.FromSeconds(5), cancellationToken: Ct);
        Assert.Contains(alice.UserId, added.DeviceLists.Changed);
        QueryKeysResponseCheck(await bob.QueryKeysAsync([alice.UserId], Ct), alice.UserId, 2);

        // Act and assert: alice deletes it, then bob leaves
        await aliceOther.Client.LogoutAsync(Ct);
        SyncResponse deleted = await bob.SyncAsync(added.NextBatch, TimeSpan.FromSeconds(5), cancellationToken: Ct);
        Assert.Contains(alice.UserId, deleted.DeviceLists.Changed);
        QueryKeysResponseCheck(await bob.QueryKeysAsync([alice.UserId], Ct), alice.UserId, 1);
        await bob.LeaveRoomAsync(roomId, cancellationToken: Ct);
        SyncResponse left = await bob.SyncAsync(deleted.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        Assert.Contains(alice.UserId, left.DeviceLists.Left);
        using HttpClient http = Authorized(server, bob);
        JsonNode changes = JsonNode.Parse(await http.GetStringAsync($"_matrix/client/v3/keys/changes?from={start}&to={left.NextBatch}", Ct))!;
        Assert.Contains(alice.UserId, changes["left"]!.AsArray().Select(u => u!.GetValue<string>()));
    }

    /// <summary>Recovery set up on one device lets a new login restore cross-signing and the key backup, and reads an old message.</summary>
    [Fact]
    public async Task RecoverFromBackup()
    {
        // Arrange: one device sends a message only it has the key for, and backs it up
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device first = await Device.RegisterAsync(server, "alice");
        string recoveryKey = await first.Machine.SetUpRecoveryAsync(Password, Ct);
        string roomId = await CreateEncryptedRoomAsync(first.Client);
        string secret = $"backed up {Guid.NewGuid():N}";
        EncryptedContent content = await first.Machine.EncryptRoomEventAsync(roomId, EventTypes.RoomMessage, Text(secret), [], cancellationToken: Ct);
        string eventId = await first.Client.SendEventAsync(roomId, EventTypes.RoomEncrypted, content, cancellationToken: Ct);
        Assert.True(await first.Machine.BackUpRoomKeysAsync(Ct) >= 1);

        // Act: a new login recovers
        using Device second = await Device.LoginAsync(server, first.UserId);
        RecoveryResult result = await second.Machine.RecoverAsync(recoveryKey, Ct);
        RoomEventDecryption decrypted = await second.Machine.DecryptRoomEventAsync(roomId, await second.Client.GetEventAsync(roomId, eventId, Ct), Ct);

        // Assert
        Assert.True(result.CrossSigningRestored && result.BackupRestored);
        Assert.True(result.KeysImported >= 1);
        Assert.Equal(DecryptionStatus.Decrypted, decrypted.Status);
        Assert.True(decrypted.Event.TryGetContent(out MessageContent? message));
        Assert.Equal(secret, message.Body);
        Assert.True((await second.Machine.GetSecurityStatusAsync(Ct)).ThisDeviceCrossSigned);
    }

    /// <summary>A cross-signed device asks another for a key it missed, and gets it forwarded over Olm.</summary>
    [Fact]
    public async Task ForwardKeyBetweenOwnDevices()
    {
        // Arrange: two recovered devices; the key stays on the first
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device sender = await Device.RegisterAsync(server, "alice");
        string recoveryKey = await sender.Machine.SetUpRecoveryAsync(Password, Ct);
        using Device requester = await Device.LoginAsync(server, sender.UserId);
        await requester.Machine.RecoverAsync(recoveryKey, Ct);
        string roomId = await CreateEncryptedRoomAsync(sender.Client);
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

    /// <summary>Two users verify with emoji; the verifier's user-signing key signs the other's identity, so the verifier's other devices trust the other user's devices too.</summary>
    [Fact]
    public async Task CrossSignOtherUser()
    {
        // Arrange: recovered devices of two users
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device aliceDevice = await Device.RegisterAsync(server, "alice");
        string aliceKey = await aliceDevice.Machine.SetUpRecoveryAsync(Password, Ct);
        using Device bobDevice = await Device.RegisterAsync(server, "bob");
        await bobDevice.Machine.SetUpRecoveryAsync(Password, Ct);
        Verification? incoming = null;
        bobDevice.Machine.VerificationChanged += v => incoming ??= v;

        // Act: request the one device, accept, compare, confirm
        Verification outgoing = await aliceDevice.Machine.RequestVerificationAsync(bobDevice.UserId, bobDevice.Client.DeviceId, Ct);
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
        using Device aliceOther = await Device.LoginAsync(server, aliceDevice.UserId);
        await aliceOther.Machine.RecoverAsync(aliceKey, Ct);
        await aliceOther.Machine.TrackUsersAsync([bobDevice.UserId], Ct);
        Assert.True(aliceOther.Machine.IsIdentityTrusted(aliceOther.Machine.GetIdentity(bobDevice.UserId)!));
        DeviceInfo verified = aliceOther.Machine.GetDevices(bobDevice.UserId).Single(d => d.DeviceId == bobDevice.Client.DeviceId);
        Assert.Equal(DeviceTrust.CrossSigned, aliceOther.Machine.GetDeviceTrust(verified));
    }

    /// <summary>Replacing cross-signing keys needs the password, and signatures that do not verify are refused.</summary>
    [Fact]
    public async Task ProtectCrossSigningKeys()
    {
        // Arrange: recovery sets up cross-signing
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        using Device alice = await Device.RegisterAsync(server, "alice");
        await alice.Machine.SetUpRecoveryAsync(Password, Ct);
        using HttpClient http = Authorized(server, alice.Client);
        JsonObject query = JsonNode.Parse(await (await http.PostAsync("_matrix/client/v3/keys/query", Json(new JsonObject { ["device_keys"] = new JsonObject { [alice.UserId] = new JsonArray() } }), Ct)).Content.ReadAsStringAsync(Ct))!.AsObject();
        JsonObject master = query["master_keys"]![alice.UserId]!.AsObject();
        JsonObject device = query["device_keys"]![alice.UserId]![alice.Client.DeviceId!]!.AsObject();

        // Act: replace the master key without a password; upload a forged device signature
        JsonObject forgedMaster = (JsonObject)master.DeepClone();
        forgedMaster["keys"] = new JsonObject { ["ed25519:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        HttpResponseMessage replace = await http.PostAsync("_matrix/client/v3/keys/device_signing/upload", Json(new JsonObject { ["master_key"] = forgedMaster }), Ct);
        JsonObject forgedDevice = (JsonObject)device.DeepClone();
        forgedDevice.Remove("unsigned");
        string selfSigning = query["self_signing_keys"]![alice.UserId]!["keys"]!.AsObject().Single().Key;
        forgedDevice["signatures"] = new JsonObject { [alice.UserId] = new JsonObject { [selfSigning] = Convert.ToBase64String(new byte[64]).TrimEnd('=') } };
        JsonObject signatures = JsonNode.Parse(await (await http.PostAsync("_matrix/client/v3/keys/signatures/upload", Json(new JsonObject { [alice.UserId] = new JsonObject { [alice.Client.DeviceId!] = forgedDevice } }), Ct)).Content.ReadAsStringAsync(Ct))!.AsObject();

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, replace.StatusCode);
        Assert.Equal("M_INVALID_SIGNATURE", signatures["failures"]![alice.UserId]![alice.Client.DeviceId!]!["errcode"]!.GetValue<string>());
    }

    /// <summary>Backups keep the better key, refuse uploads to an old version with the current one named, and counts keys.</summary>
    [Fact]
    public async Task KeyBackupRules()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await RegisterAsync(server, "alice");
        using HttpClient http = Authorized(server, alice);
        string old = await alice.CreateKeyBackupVersionAsync("m.megolm_backup.v1.curve25519-aes-sha2", new JsonObject { ["public_key"] = "abc" }, Ct);
        string current = await alice.CreateKeyBackupVersionAsync("m.megolm_backup.v1.curve25519-aes-sha2", new JsonObject { ["public_key"] = "def" }, Ct);
        static JsonObject Key(int index, bool verified, string marker) => new() { ["first_message_index"] = index, ["forwarded_count"] = 0, ["is_verified"] = verified, ["session_data"] = new JsonObject { ["marker"] = marker } };

        // Act
        HttpResponseMessage first = await http.PutAsync($"_matrix/client/v3/room_keys/keys/!room:test.local/session?version={current}", Json(Key(5, false, "first")), Ct);
        await http.PutAsync($"_matrix/client/v3/room_keys/keys/!room:test.local/session?version={current}", Json(Key(9, false, "worse")), Ct);
        await http.PutAsync($"_matrix/client/v3/room_keys/keys/!room:test.local/session?version={current}", Json(Key(9, true, "verified")), Ct);
        HttpResponseMessage stale = await http.PutAsync($"_matrix/client/v3/room_keys/keys/!room:test.local/session?version={old}", Json(Key(0, true, "stale")), Ct);
        JsonNode stored = JsonNode.Parse(await http.GetStringAsync($"_matrix/client/v3/room_keys/keys/!room:test.local/session?version={current}", Ct))!;
        KeyBackupVersion? version = await alice.GetKeyBackupVersionAsync(Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("verified", stored["session_data"]!["marker"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        JsonNode staleBody = JsonNode.Parse(await stale.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("M_WRONG_ROOM_KEYS_VERSION", staleBody["errcode"]!.GetValue<string>());
        Assert.Equal(current, staleBody["current_version"]!.GetValue<string>());
        Assert.Equal(current, version?.Version);
        Assert.Equal(1, version?.Count);
    }

    /// <summary>Register a user without encryption.</summary>
    private static async Task<MatrixClient> RegisterAsync(TestHomeserver server, string name)
    {
        // Register logs in
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync(name, Password, cancellationToken: Ct);
        // Return client
        return client;
    }

    /// <summary>Create a private encrypted room, inviting <paramref name="invite"/>.</summary>
    private static Task<string> CreateEncryptedRoomAsync(MatrixClient client, MatrixClient? invite = null)
    {
        // Encrypted from the start
        InitialStateEvent encryption = new() { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } };
        // Return room ID
        return client.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PrivateChat, InitialState = [encryption], Invite = invite is null ? null : [invite.UserId!] }, Ct);
    }

    /// <summary>Return the key IDs claimed for the device.</summary>
    private static List<string> KeyIds(KeysClaimResponse response, Device device) =>
        [.. response.OneTimeKeys.GetValueOrDefault(device.UserId)?.GetValueOrDefault(device.Client.DeviceId!)?.Keys ?? []];

    /// <summary>Assert that a key query lists <paramref name="count"/> devices of the user.</summary>
    private static void QueryKeysResponseCheck(KeysQueryResponse response, string userId, int count) => Assert.Equal(count, response.DeviceKeys.GetValueOrDefault(userId)?.Count ?? 0);

    /// <summary>Return an HTTP client with the client's access token.</summary>
    private static HttpClient Authorized(TestHomeserver server, MatrixClient client)
    {
        // Bearer token
        HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", client.Session!.AccessToken);
        // Return client
        return http;
    }

    /// <summary>Return JSON request content.</summary>
    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Text message content.</summary>
    private static MessageContent Text(string body) => new() { MessageType = MessageTypes.Text, Body = body };

    /// <summary>Sync each device once.</summary>
    private static async Task SyncAllAsync(params Device[] devices)
    {
        // In order
        foreach (Device device in devices) await device.SyncAsync();
    }

    /// <summary>A logged-in device with an in-memory <see cref="OlmMachine"/> and a sync position.</summary>
    private sealed class Device : IDisposable
    {
        private string? since;

        /// <summary>Wrap a client and its machine.</summary>
        private Device(MatrixClient client, OlmMachine machine)
        {
            // Store parts
            Client = client;
            Machine = machine;
        }

        /// <summary>Logged-in client.</summary>
        public MatrixClient Client { get; }
        /// <summary>Encryption of the device.</summary>
        public OlmMachine Machine { get; }
        /// <summary>User ID.</summary>
        public string UserId => Client.UserId!;

        /// <summary>Register a user and start encryption on its first device.</summary>
        public static async Task<Device> RegisterAsync(TestHomeserver server, string name)
        {
            // Register logs in
            MatrixClient client = server.CreateClient();
            await client.RegisterAsync(name, Password, cancellationToken: Ct);
            // Return device
            return await StartAsync(client);
        }

        /// <summary>Log in another device of a user and start encryption on it.</summary>
        public static async Task<Device> LoginAsync(TestHomeserver server, string userId)
        {
            // Password login
            MatrixClient client = server.CreateClient();
            await client.LoginWithPasswordAsync(userId, Password, cancellationToken: Ct);
            // Return device
            return await StartAsync(client);
        }

        /// <summary>Sync once with a short wait and feeds the result to the machine.</summary>
        public async Task SyncAsync()
        {
            // Incremental sync
            SyncResponse sync = await Client.SyncAsync(since, TimeSpan.FromMilliseconds(200), Filter, cancellationToken: Ct);
            since = sync.NextBatch;
            await Machine.ReceiveSyncAsync(sync, Ct);
        }

        /// <summary>Free the machine.</summary>
        public void Dispose() => Machine.Dispose();

        /// <summary>Upload the device's keys and take a sync position.</summary>
        private static async Task<Device> StartAsync(MatrixClient client)
        {
            // Machine with fresh keys
            OlmMachine machine = await OlmMachine.CreateAsync(new MatrixKeyTransport(client), new InMemoryCryptoStore(), client.UserId!, client.DeviceId!, RandomNumberGenerator.GetBytes(32), cancellationToken: Ct);
            await machine.UploadInitialKeysAsync(Ct);
            Device device = new(client, machine);
            device.since = (await client.SyncAsync(filter: Filter, cancellationToken: Ct)).NextBatch;
            // Return device
            return device;
        }
    }
}
