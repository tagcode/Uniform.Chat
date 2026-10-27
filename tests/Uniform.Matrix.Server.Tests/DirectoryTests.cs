namespace Uniform.Matrix.Server.Tests;

using System.Net;
using Microsoft.Extensions.Time.Testing;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of profiles, presence, room aliases, the public room directory and user search, through our own client.</summary>
public class DirectoryTests
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

    /// <summary>A new display name and avatar show in the profile and in the member events of joined rooms; others' profiles cannot be changed.</summary>
    [Fact]
    public async Task Profiles()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act
        await alice.SetDisplayNameAsync("Alice Liddell", Ct);
        await alice.SetAvatarUrlAsync("mxc://test.local/rabbit", Ct);
        ProfileResponse profile = await bob.GetProfileAsync(alice.UserId!, Ct);
        IReadOnlyDictionary<string, RoomMember> members = await bob.GetJoinedMembersAsync(roomId, Ct);

        // Assert
        Assert.Equal("Alice Liddell", profile.DisplayName);
        Assert.Equal("mxc://test.local/rabbit", profile.AvatarUrl);
        Assert.Equal("Alice Liddell", members[alice.UserId!].DisplayName);
        Assert.Equal("mxc://test.local/rabbit", members[alice.UserId!].AvatarUrl);
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", bob.Session!.AccessToken);
        HttpResponseMessage other = await http.PutAsync($"_matrix/client/v3/profile/{Uri.EscapeDataString(alice.UserId!)}/displayname", JsonContent("""{"displayname":"Mallory"}"""), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        MatrixException unknown = await Assert.ThrowsAsync<MatrixException>(() => bob.GetProfileAsync("@nobody:test.local", Ct));
        Assert.Equal(ErrorCodes.NotFound, unknown.Errcode);
    }

    /// <summary>Presence set by a user is read back by others; users who never set it are offline.</summary>
    [Fact]
    public async Task Presence()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");

        // Act
        await alice.SetPresenceAsync("unavailable", "lunch", Ct);
        PresenceContent away = await bob.GetPresenceAsync(alice.UserId!, Ct);
        PresenceContent never = await alice.GetPresenceAsync(bob.UserId!, Ct);

        // Assert
        Assert.Equal("unavailable", away.Presence);
        Assert.Equal("lunch", away.StatusMsg);
        Assert.Equal("offline", never.Presence);
    }

    /// <summary>Syncing marks a user online for the people sharing a room; a chosen status reaches them; strangers see nothing; a user who stops syncing goes offline.</summary>
    [Fact]
    public async Task PresenceInSync()
    {
        // Arrange: Alice and Bob share a room, Carol does not; a fake clock moves time on
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open, clock);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act: Alice syncs, which marks her online
        await alice.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse bobFirst = await bob.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse carolFirst = await carol.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);

        // Assert: Bob sees her online, Carol does not see her
        PresenceEvent online = Assert.Single(bobFirst.Presence.Events, e => e.Sender == alice.UserId);
        Assert.Equal("online", online.Content.GetProperty("presence").GetString());
        Assert.True(online.Content.GetProperty("currently_active").GetBoolean());
        Assert.DoesNotContain(carolFirst.Presence.Events, e => e.Sender == alice.UserId);

        // Act and assert: a chosen status reaches Bob's incremental sync, and stays while Alice syncs
        await alice.SetPresenceAsync("unavailable", "lunch", Ct);
        await alice.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse bobNext = await bob.SyncAsync(bobFirst.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        PresenceEvent away = Assert.Single(bobNext.Presence.Events, e => e.Sender == alice.UserId);
        Assert.Equal("unavailable", away.Content.GetProperty("presence").GetString());
        Assert.Equal("lunch", away.Content.GetProperty("status_msg").GetString());

        // Act and assert: no syncs for longer than the limit takes her offline, which wakes Bob's waiting sync
        Task<SyncResponse> waiting = bob.SyncAsync(bobNext.NextBatch, TimeSpan.FromSeconds(30), cancellationToken: Ct);
        clock.Advance(TimeSpan.FromMinutes(3));
        SyncResponse bobLast = await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("offline", Assert.Single(bobLast.Presence.Events, e => e.Sender == alice.UserId).Content.GetProperty("presence").GetString());
    }

    /// <summary>Aliases are created by members, resolve, list per room, are unique, and are deleted by their creator but not by others.</summary>
    [Fact]
    public async Task Aliases()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act
        await bob.CreateRoomAliasAsync("#kitchen:test.local", roomId, Ct);
        RoomAliasResponse resolved = await alice.ResolveRoomAliasAsync("#kitchen:test.local", Ct);
        IReadOnlyList<string> aliases = await alice.GetRoomAliasesAsync(roomId, Ct);

        // Assert: created and resolved
        Assert.Equal(roomId, resolved.RoomId);
        Assert.Equal(["test.local"], resolved.Servers);
        Assert.Equal(["#kitchen:test.local"], aliases);
        MatrixException taken = await Assert.ThrowsAsync<MatrixException>(() => alice.CreateRoomAliasAsync("#kitchen:test.local", roomId, Ct));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        MatrixException foreign = await Assert.ThrowsAsync<MatrixException>(() => alice.CreateRoomAliasAsync("#kitchen:elsewhere.org", roomId, Ct));
        Assert.Equal(ErrorCodes.InvalidParam, foreign.Errcode);

        // Act and assert: room admins and creators may delete, others not
        MatrixClient carol = await UserAsync(server, "carol");
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);
        MatrixException refused = await Assert.ThrowsAsync<MatrixException>(() => carol.DeleteRoomAliasAsync("#kitchen:test.local", Ct));
        Assert.Equal(ErrorCodes.Forbidden, refused.Errcode);
        await alice.DeleteRoomAliasAsync("#kitchen:test.local", Ct);
        MatrixException gone = await Assert.ThrowsAsync<MatrixException>(() => alice.ResolveRoomAliasAsync("#kitchen:test.local", Ct));
        Assert.Equal(ErrorCodes.NotFound, gone.Errcode);
    }

    /// <summary>Published rooms are listed with their name and member count, searched, and paged; unlisted rooms are not; only admins change the listing.</summary>
    [Fact]
    public async Task PublicRooms()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string kitchen = await alice.CreateRoomAsync(new CreateRoomRequest { Visibility = "public", Preset = RoomPresets.PublicChat, Name = "Kitchen", Topic = "Food", RoomAliasName = "kitchen" }, Ct);
        string garden = await alice.CreateRoomAsync(new CreateRoomRequest { Visibility = "public", Preset = RoomPresets.PublicChat, Name = "Garden" }, Ct);
        string secret = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Secret" }, Ct);
        await bob.JoinRoomAsync(kitchen, cancellationToken: Ct);

        // Act
        PublicRoomsResponse all = await bob.GetPublicRoomsAsync(cancellationToken: Ct);
        PublicRoomsResponse first = await bob.GetPublicRoomsAsync(limit: 1, cancellationToken: Ct);
        PublicRoomsResponse second = await bob.GetPublicRoomsAsync(limit: 1, since: first.NextBatch, cancellationToken: Ct);
        PublicRoomsResponse search = await bob.GetPublicRoomsAsync(searchTerm: "food", cancellationToken: Ct);

        // Assert
        Assert.Equal([kitchen, garden], all.Chunk.Select(r => r.RoomId));
        PublicRoom listed = all.Chunk[0];
        Assert.Equal("Kitchen", listed.Name);
        Assert.Equal("#kitchen:test.local", listed.CanonicalAlias);
        Assert.Equal(2, listed.NumJoinedMembers);
        Assert.DoesNotContain(all.Chunk, r => r.RoomId == secret);
        Assert.Equal([kitchen], first.Chunk.Select(r => r.RoomId));
        Assert.Equal([garden], second.Chunk.Select(r => r.RoomId));
        Assert.Null(second.NextBatch);
        Assert.Equal([kitchen], search.Chunk.Select(r => r.RoomId));

        // Act and assert: listing changes
        MatrixException refused = await Assert.ThrowsAsync<MatrixException>(() => bob.SetRoomDirectoryVisibilityAsync(kitchen, false, Ct));
        Assert.Equal(ErrorCodes.Forbidden, refused.Errcode);
        await alice.SetRoomDirectoryVisibilityAsync(garden, false, Ct);
        await alice.SetRoomDirectoryVisibilityAsync(secret, true, Ct);
        Assert.Equal([kitchen, secret], (await bob.GetPublicRoomsAsync(cancellationToken: Ct)).Chunk.Select(r => r.RoomId));
    }

    /// <summary>User search finds users by ID or display name among those sharing a room or in public rooms, but not strangers.</summary>
    [Fact]
    public async Task SearchUsers()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        MatrixClient dave = await UserAsync(server, "dave");
        await bob.SetDisplayNameAsync("Robert Builder", Ct);
        string shared = await alice.CreateRoomAsync(new CreateRoomRequest { Invite = [bob.UserId!] }, Ct);
        await bob.JoinRoomAsync(shared, cancellationToken: Ct);
        await carol.CreateRoomAsync(new CreateRoomRequest { Visibility = "public", Preset = RoomPresets.PublicChat }, Ct);
        await dave.CreateRoomAsync(new CreateRoomRequest(), Ct);

        // Act
        UserDirectoryResponse byName = await alice.SearchUsersAsync("builder", cancellationToken: Ct);
        UserDirectoryResponse byId = await alice.SearchUsersAsync("carol", cancellationToken: Ct);
        UserDirectoryResponse stranger = await alice.SearchUsersAsync("dave", cancellationToken: Ct);
        UserDirectoryResponse limited = await alice.SearchUsersAsync("test.local", limit: 1, cancellationToken: Ct);

        // Assert
        Assert.Equal([bob.UserId], byName.Results.Select(r => r.UserId));
        Assert.Equal([carol.UserId], byId.Results.Select(r => r.UserId));
        Assert.Empty(stranger.Results);
        Assert.Single(limited.Results);
        Assert.True(limited.Limited);
    }

    /// <summary>Return JSON request content.</summary>
    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
