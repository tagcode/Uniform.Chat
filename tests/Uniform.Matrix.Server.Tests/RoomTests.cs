namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of room creation, membership, messages, state and redaction, through our own client.</summary>
public class RoomTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };

    /// <summary>Register a user and return its client.</summary>
    private static async Task<MatrixClient> UserAsync(TestHomeserver server, string name)
    {
        // Register logs in
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync(name, "password", cancellationToken: Ct);
        // Return client
        return client;
    }

    /// <summary>A new room has the creator joined, the requested name and topic, and the preset's join rule.</summary>
    [Fact]
    public async Task CreateRoom()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");

        // Act
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Topic = "Food", Preset = RoomPresets.PrivateChat }, Ct);
        IReadOnlyList<ClientEvent> state = await alice.GetStateAsync(roomId, Ct);

        // Assert
        Assert.EndsWith($":{TestHomeserver.ServerName}", roomId);
        Assert.Contains(roomId, await alice.GetJoinedRoomsAsync(Ct));
        Assert.Contains(state, e => e.Type == EventTypes.RoomCreate);
        Assert.Contains(state, e => e.Type == EventTypes.RoomMember && e.StateKey == alice.UserId);
        Assert.Equal("Kitchen", (await alice.TryGetStateEventAsync(roomId, EventTypes.RoomName, cancellationToken: Ct))?.GetProperty("name").GetString());
        Assert.Equal("invite", (await alice.TryGetStateEventAsync(roomId, "m.room.join_rules", cancellationToken: Ct))?.GetProperty("join_rule").GetString());
        Assert.Equal(100, (await alice.TryGetStateEventAsync(roomId, EventTypes.RoomPowerLevels, cancellationToken: Ct))?.GetProperty("users").GetProperty(alice.UserId!).GetInt32());
    }

    /// <summary>A power level change keeps the other levels, including fields the typed content does not name; a moderator can kick, an ordinary member cannot; the room avatar is set and removed.</summary>
    [Fact]
    public async Task PowerLevelsAndAvatar()
    {
        // Arrange: Alice's public room with Bob and Carol
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);
        PowerLevelsContent before = (await alice.GetPowerLevelsAsync(roomId, Ct))!;

        // Act: Bob becomes moderator and kicks Carol, who cannot kick back
        await alice.SetUserPowerLevelAsync(roomId, bob.UserId!, PowerLevelsContent.Moderator, Ct);
        MatrixException refused = await Assert.ThrowsAsync<MatrixException>(() => carol.KickAsync(roomId, bob.UserId!, cancellationToken: Ct));
        await bob.KickAsync(roomId, carol.UserId!, "testing", Ct);
        PowerLevelsContent after = (await alice.GetPowerLevelsAsync(roomId, Ct))!;

        // Assert: Bob's level changed and nothing else
        Assert.Equal(ErrorCodes.Forbidden, refused.Errcode);
        Assert.Equal(PowerLevelsContent.Moderator, after.GetUserLevel(bob.UserId!));
        Assert.Equal(PowerLevelsContent.Admin, after.GetUserLevel(alice.UserId!));
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.Extra?.Keys.Order(), after.Extra?.Keys.Order());
        Assert.DoesNotContain(carol.UserId!, (await alice.GetJoinedMembersAsync(roomId, Ct)).Keys);

        // Act and assert: back to an ordinary member removes the entry
        await alice.SetUserPowerLevelAsync(roomId, bob.UserId!, 0, Ct);
        Assert.False((await alice.GetPowerLevelsAsync(roomId, Ct))!.Users!.ContainsKey(bob.UserId!));

        // Act and assert: avatar set, then removed
        MxcUri image = await alice.UploadMediaAsync(new MemoryStream([1, 2, 3]), "image/png", "a.png", Ct);
        await alice.SetRoomAvatarAsync(roomId, image, Ct);
        Assert.Equal(image.ToString(), (await alice.TryGetStateEventAsync(roomId, EventTypes.RoomAvatar, cancellationToken: Ct))?.GetProperty("url").GetString());
        await alice.SetRoomAvatarAsync(roomId, null, Ct);
        Assert.False((await alice.TryGetStateEventAsync(roomId, EventTypes.RoomAvatar, cancellationToken: Ct))?.TryGetProperty("url", out _));
    }

    /// <summary>Invite-only rooms need an invite; public rooms with an alias can be joined by the alias.</summary>
    [Fact]
    public async Task JoinRooms()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string privateRoom = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PrivateChat }, Ct);
        string publicRoom = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, RoomAliasName = "lobby" }, Ct);

        // Act and assert: invite-only
        MatrixException refused = await Assert.ThrowsAsync<MatrixException>(() => bob.JoinRoomAsync(privateRoom, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.Forbidden, refused.Errcode);
        await alice.InviteAsync(privateRoom, bob.UserId!, cancellationToken: Ct);
        Assert.Equal(privateRoom, await bob.JoinRoomAsync(privateRoom, cancellationToken: Ct));

        // Act and assert: public by alias
        Assert.Equal(publicRoom, await bob.JoinRoomAsync($"#lobby:{TestHomeserver.ServerName}", cancellationToken: Ct));
        IReadOnlyDictionary<string, RoomMember> members = await bob.GetJoinedMembersAsync(publicRoom, Ct);
        Assert.Equal(["@alice:test.local", "@bob:test.local"], members.Keys.Order());
        Assert.Equal("bob", members["@bob:test.local"].DisplayName);
        MatrixException taken = await Assert.ThrowsAsync<MatrixException>(() => bob.CreateRoomAsync(new CreateRoomRequest { RoomAliasName = "lobby" }, Ct));
        Assert.Equal(ErrorCodes.RoomInUse, taken.Errcode);
    }

    /// <summary>Messages are stored and reads back with their transaction ID; a retried transaction returns the same event; non-members cannot send.</summary>
    [Fact]
    public async Task SendMessages()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient eve = await UserAsync(server, "eve");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);

        // Act
        string first = await alice.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "hello" }, "txn1", Ct);
        string retried = await alice.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "hello" }, "txn1", Ct);
        ClientEvent read = await alice.GetEventAsync(roomId, first, Ct);

        // Assert
        Assert.Equal(first, retried);
        Assert.StartsWith("$", first);
        Assert.Equal("hello", read.TryGetContent(out MessageContent? message) ? message.Body : null);
        Assert.Equal("txn1", read.Unsigned?.TransactionId);
        MatrixException outsider = await Assert.ThrowsAsync<MatrixException>(() => eve.SendTextAsync(roomId, "spam", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        await Assert.ThrowsAsync<MatrixException>(() => eve.GetEventAsync(roomId, first, Ct));
    }

    /// <summary>Kicked users leave; banned users cannot return until unbanned; only the admin may do either.</summary>
    [Fact]
    public async Task KickAndBan()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act and assert
        await Assert.ThrowsAsync<MatrixException>(() => bob.KickAsync(roomId, alice.UserId!, cancellationToken: Ct));
        await alice.KickAsync(roomId, bob.UserId!, "too loud", Ct);
        Assert.DoesNotContain(roomId, await bob.GetJoinedRoomsAsync(Ct));
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await alice.BanAsync(roomId, bob.UserId!, cancellationToken: Ct);
        await Assert.ThrowsAsync<MatrixException>(() => bob.JoinRoomAsync(roomId, cancellationToken: Ct));
        await alice.UnbanAsync(roomId, bob.UserId!, cancellationToken: Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
    }

    /// <summary>Users redact their own events; others need the redact level; redacted events lose their content and say why.</summary>
    [Fact]
    public async Task RedactEvents()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        string aliceMessage = await alice.SendTextAsync(roomId, "secret", Ct);
        string bobMessage = await bob.SendTextAsync(roomId, "oops", Ct);

        // Act and assert
        await Assert.ThrowsAsync<MatrixException>(() => bob.RedactAsync(roomId, aliceMessage, cancellationToken: Ct));
        await bob.RedactAsync(roomId, bobMessage, "typo", cancellationToken: Ct);
        await alice.RedactAsync(roomId, aliceMessage, cancellationToken: Ct);
        ClientEvent redacted = await bob.GetEventAsync(roomId, bobMessage, Ct);
        Assert.Equal("{}", redacted.Content.GetRawText());
        Assert.NotNull(redacted.Unsigned?.RedactedBecause);
        Assert.Equal(bobMessage, redacted.Unsigned!.RedactedBecause!.Redacts);
    }

    /// <summary>Leaving keeps the state as it was at leaving; forgetting needs leaving first.</summary>
    [Fact]
    public async Task LeaveAndForget()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Before" }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act
        await Assert.ThrowsAsync<MatrixException>(() => bob.ForgetRoomAsync(roomId, Ct));
        await bob.LeaveRoomAsync(roomId, cancellationToken: Ct);
        await alice.SetRoomNameAsync(roomId, "After", Ct);
        IReadOnlyList<ClientEvent> seen = await bob.GetStateAsync(roomId, Ct);
        await bob.ForgetRoomAsync(roomId, Ct);

        // Assert
        Assert.Equal("Before", seen.Single(e => e.Type == EventTypes.RoomName).Content.GetProperty("name").GetString());
        Assert.Equal("leave", seen.Single(e => e.Type == EventTypes.RoomMember && e.StateKey == bob.UserId).Content.GetProperty("membership").GetString());
    }

    /// <summary>Unsupported room versions and unknown users are refused.</summary>
    [Fact]
    public async Task RefuseBadRequests()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { RoomVersion = "11" }, Ct);

        // Act
        MatrixException version = await Assert.ThrowsAsync<MatrixException>(() => alice.CreateRoomAsync(new CreateRoomRequest { RoomVersion = "1" }, Ct));
        MatrixException unknown = await Assert.ThrowsAsync<MatrixException>(() => alice.InviteAsync(roomId, "@nobody:test.local", cancellationToken: Ct));
        JsonElement? create = await alice.TryGetStateEventAsync(roomId, EventTypes.RoomCreate, cancellationToken: Ct);

        // Assert
        Assert.Equal(ErrorCodes.UnsupportedRoomVersion, version.Errcode);
        Assert.Equal(ErrorCodes.NotFound, unknown.Errcode);
        Assert.Equal("11", create?.GetProperty("room_version").GetString());
        Assert.False(create?.TryGetProperty("creator", out _));
    }
}
