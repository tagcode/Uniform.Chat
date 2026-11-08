namespace Uniform.Matrix.Server.Tests;

using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>Tests of the remote admin API: whois, suspension and locks of the spec, and the subset of Synapse's admin API, through <see cref="MatrixClient"/>.</summary>
public class RemoteAdminTests
{
    private const string Password = "correct horse";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Return a server with a logged-in admin and a logged-in user Bob.</summary>
    private static async Task<(TestHomeserver Server, MatrixClient Admin, MatrixClient Bob)> StartAsync(IReadOnlyDictionary<string, string?>? settings = null)
    {
        // Accounts made by the operator
        TestHomeserver server = await TestHomeserver.StartAsync(settings);
        AdminService service = server.Services.GetRequiredService<AdminService>();
        await service.CreateUserAsync("admin", Password, admin: true, Ct);
        await service.CreateUserAsync("bob", Password, admin: false, Ct);
        MatrixClient admin = server.CreateClient();
        await admin.LoginWithPasswordAsync("admin", Password, cancellationToken: Ct);
        MatrixClient bob = server.CreateClient();
        await bob.LoginWithPasswordAsync("bob", Password, cancellationToken: Ct);
        // Return server and clients
        return (server, admin, bob);
    }

    /// <summary>Only admins use the admin API; whois shows the devices of anyone to admins, and one's own to anyone.</summary>
    [Fact]
    public async Task AllowOnlyAdmins()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient bob) = await StartAsync();
        await using TestHomeserver _ = server;

        // Act
        bool adminIs = await admin.IsServerAdminAsync(Ct);
        bool bobIs = await bob.IsServerAdminAsync(Ct);
        MatrixException forbidden = await Assert.ThrowsAsync<MatrixException>(() => bob.GetAdminAccountsAsync(cancellationToken: Ct));
        WhoisResponse whois = await admin.WhoisAsync(bob.UserId!, Ct);
        WhoisResponse own = await bob.WhoisAsync(bob.UserId!, Ct);

        // Assert
        Assert.True(adminIs);
        Assert.False(bobIs);
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Errcode);
        Assert.Equal(bob.Session!.DeviceId, Assert.Single(whois.Devices).Key);
        Assert.Single(own.Devices);
        await Assert.ThrowsAsync<MatrixException>(() => bob.WhoisAsync(admin.UserId!, Ct));
    }

    /// <summary>Admins list, create and change accounts: a new account logs in, a reset password replaces the old, admin rights come and go, and an admin cannot shut themselves out.</summary>
    [Fact]
    public async Task ManageAccounts()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient bob) = await StartAsync();
        await using TestHomeserver _ = server;

        // Act
        AdminAccount carol = await admin.CreateAdminAccountAsync("@carol:test.local", "first", "Carol", cancellationToken: Ct);
        await admin.ResetPasswordAsync(carol.Name, "second", cancellationToken: Ct);
        await admin.SetServerAdminAsync(bob.UserId!, true, Ct);
        AdminAccountsResponse all = await admin.GetAdminAccountsAsync(cancellationToken: Ct);
        AdminAccountsResponse found = await admin.GetAdminAccountsAsync("caro", cancellationToken: Ct);

        // Assert
        Assert.Equal("Carol", carol.DisplayName);
        await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync("carol", "first", cancellationToken: Ct));
        await server.CreateClient().LoginWithPasswordAsync("carol", "second", cancellationToken: Ct);
        Assert.True(await bob.IsServerAdminAsync(Ct));
        Assert.Equal(3, all.Total);
        Assert.Equal("@carol:test.local", Assert.Single(found.Users).Name);
        MatrixException self = await Assert.ThrowsAsync<MatrixException>(() => admin.SetServerAdminAsync(admin.UserId!, false, Ct));
        Assert.Equal(ErrorCodes.Forbidden, self.Errcode);
    }

    /// <summary>A suspended user can read and leave but not send or join; a locked user is refused with a soft logout until unlocked, and keeps the session.</summary>
    [Fact]
    public async Task SuspendAndLock()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient bob) = await StartAsync();
        await using TestHomeserver _ = server;
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        string otherRoom = await admin.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);

        // Act and assert: suspension
        await admin.SetSuspendedAsync(bob.UserId!, true, Ct);
        Assert.True(await admin.IsSuspendedAsync(bob.UserId!, Ct));
        MatrixException send = await Assert.ThrowsAsync<MatrixException>(() => bob.SendTextAsync(roomId, "hello", Ct));
        Assert.Equal(ErrorCodes.UserSuspended, send.Errcode);
        MatrixException join = await Assert.ThrowsAsync<MatrixException>(() => bob.JoinRoomAsync(otherRoom, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.UserSuspended, join.Errcode);
        await bob.SyncAsync(cancellationToken: Ct);
        await admin.SetSuspendedAsync(bob.UserId!, false, Ct);
        await bob.SendTextAsync(roomId, "back", Ct);

        // Act and assert: lock
        await admin.SetLockedAsync(bob.UserId!, true, Ct);
        MatrixException locked = await Assert.ThrowsAsync<MatrixException>(() => bob.WhoAmIAsync(Ct));
        Assert.Equal(ErrorCodes.UserLocked, locked.Errcode);
        MatrixException login = await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync("bob", Password, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.UserLocked, login.Errcode);
        await admin.SetLockedAsync(bob.UserId!, false, Ct);
        await bob.WhoAmIAsync(Ct);
    }

    /// <summary>Tokens admins create register their number of times and then no more; deleted tokens stop working.</summary>
    [Fact]
    public async Task ManageRegistrationTokens()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient _) = await StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Token" });
        await using TestHomeserver __ = server;

        // Act
        AdminRegistrationToken once = await admin.CreateRegistrationTokenAsync("once", usesAllowed: 1, cancellationToken: Ct);
        AdminRegistrationToken random = await admin.CreateRegistrationTokenAsync(cancellationToken: Ct);
        await server.CreateClient().RegisterAsync("dave", Password, registrationToken: once.Token, cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => server.CreateClient().RegisterAsync("erin", Password, registrationToken: once.Token, cancellationToken: Ct));
        await admin.DeleteRegistrationTokenAsync(random.Token, Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => server.CreateClient().RegisterAsync("erin", Password, registrationToken: random.Token, cancellationToken: Ct));
        await admin.UpdateRegistrationTokenAsync(once.Token, usesAllowed: null, expiry: null, Ct);
        await server.CreateClient().RegisterAsync("erin", Password, registrationToken: once.Token, cancellationToken: Ct);
        IReadOnlyList<AdminRegistrationToken> tokens = await admin.GetRegistrationTokensAsync(cancellationToken: Ct);

        // Assert
        Assert.Equal(16, random.Token.Length);
        AdminRegistrationToken listed = Assert.Single(tokens);
        Assert.Equal("once", listed.Token);
        Assert.Null(listed.UsesAllowed);
        Assert.Equal(2, listed.Completed);
    }

    /// <summary>Reports show for admins and can be deleted; shutting a room down removes its local members, and blocking keeps them out.</summary>
    [Fact]
    public async Task HandleReportsAndRooms()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient bob) = await StartAsync();
        await using TestHomeserver _ = server;
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Spam" }, Ct);
        await bob.ReportRoomAsync(roomId, "test", Ct);

        // Act
        AdminReportsResponse reports = await admin.GetReportsAsync(cancellationToken: Ct);
        await admin.DeleteReportAsync(reports.EventReports[0].Id, Ct);
        AdminRoomsResponse rooms = await admin.GetAdminRoomsAsync("spam", cancellationToken: Ct);
        IReadOnlyList<string> members = await admin.GetAdminRoomMembersAsync(roomId, Ct);
        AdminRoomShutdown shutdown = await admin.ShutdownRoomAsync(roomId, block: true, "Closed", Ct);

        // Assert
        Assert.Equal(roomId, Assert.Single(reports.EventReports).RoomId);
        Assert.Equal(0, (await admin.GetReportsAsync(cancellationToken: Ct)).Total);
        Assert.Equal(roomId, Assert.Single(rooms.Rooms).RoomId);
        Assert.Equal([bob.UserId!], members);
        Assert.Equal([bob.UserId!], shutdown.KickedUsers);
        Assert.DoesNotContain(roomId, await bob.GetJoinedRoomsAsync(Ct));
        MatrixException blocked = await Assert.ThrowsAsync<MatrixException>(() => bob.JoinRoomAsync(roomId, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.Forbidden, blocked.Errcode);
    }

    /// <summary>A server notice invites the user to a notices room tagged <c>m.server_notice</c>; the next notice goes to the same room.</summary>
    [Fact]
    public async Task SendServerNotices()
    {
        // Arrange
        (TestHomeserver server, MatrixClient admin, MatrixClient bob) = await StartAsync();
        await using TestHomeserver _ = server;

        // Act
        string first = await admin.SendServerNoticeAsync(bob.UserId!, "Maintenance tonight", Ct);
        SyncResponse sync = await bob.SyncAsync(cancellationToken: Ct);
        string roomId = Assert.Single(sync.Rooms.Invite).Key;
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        string second = await admin.SendServerNoticeAsync(bob.UserId!, "Done", Ct);
        TagContent tags = await bob.GetTagsAsync(roomId, Ct);
        MessagesResponse messages = await bob.GetMessagesAsync(roomId, limit: 50, cancellationToken: Ct);

        // Assert
        Assert.NotEqual(first, second);
        Assert.Contains("m.server_notice", tags.Tags.Keys);
        Assert.Contains(messages.Chunk, e => e.EventId == first);
        Assert.Contains(messages.Chunk, e => e.EventId == second);
        await Assert.ThrowsAsync<MatrixException>(() => bob.SendTextAsync(roomId, "reply", Ct));
        await Assert.ThrowsAsync<MatrixException>(() => admin.SendServerNoticeAsync("@nobody:test.local", "hi", Ct));
    }
}
