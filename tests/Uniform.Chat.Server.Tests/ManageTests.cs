namespace Uniform.Chat.Server.Tests;

using Uniform.Chat.Server.Management;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Rooms;

/// <summary>Managing a running server without stopping it: through its admin socket, which needs no account; over the network as a logged-in admin; and the settings file of a service.</summary>
public class ManageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Through the socket, the manager sees the server's state and files, creates and changes accounts, lists a room in the directory, and resolves and deletes a report.</summary>
    [Fact]
    public async Task ManageThroughSocket()
    {
        // Arrange: the manager on the socket, and a user it creates who reports their room
        await using SocketHomeserver server = await SocketHomeserver.StartAsync();
        await using HttpServerAdmin admin = await HttpServerAdmin.ConnectSocketAsync(server.Socket, Ct);
        string bob = await admin.CreateUserAsync("bob", "first password", admin: false, Ct);
        MatrixClient client = server.CreateClient();
        await client.LoginWithPasswordAsync("bob", "first password", cancellationToken: Ct);
        string roomId = await client.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" }, Ct);
        await client.ReportRoomAsync(roomId, "spam", Ct);

        // Act and assert: the state, from the server itself
        ServerSnapshot snapshot = await admin.GetSnapshotAsync(Ct);
        Assert.Equal(SocketHomeserver.ServerName, snapshot.ServerName);
        Assert.Contains(snapshot.Addresses, a => a.StartsWith("http://unix:", StringComparison.Ordinal));
        Assert.NotNull(snapshot.DataDirectory);
        Assert.NotNull(snapshot.Requests);

        // Act and assert: accounts
        Assert.Equal("@bob:test.local", bob);
        await Assert.ThrowsAsync<MatrixException>(() => admin.CreateUserAsync("bob", "again", admin: false, Ct));
        await admin.SetAdminAsync(bob, true, Ct);
        await admin.SetSuspendedAsync(bob, true, Ct);
        await admin.SetLockedAsync(bob, true, Ct);
        AdminUser changed = Assert.Single(await admin.GetUsersAsync(Ct));
        Assert.True(changed.Admin && changed.Suspended && changed.Locked);
        await admin.SetLockedAsync(bob, false, Ct);
        await admin.ResetPasswordAsync(bob, "second password", Ct);
        await server.CreateClient().LoginWithPasswordAsync("bob", "second password", cancellationToken: Ct);
        await admin.SetDeactivatedAsync(bob, true, Ct);
        Assert.True(Assert.Single(await admin.GetUsersAsync(Ct)).Deactivated);
        await admin.SetDeactivatedAsync(bob, false, Ct);
        Assert.False(Assert.Single(await admin.GetUsersAsync(Ct)).Deactivated);

        // Act and assert: the room into the public directory
        await admin.SetPublishedAsync(roomId, true, Ct);
        Assert.True(Assert.Single(await admin.GetRoomsAsync(Ct), r => r.RoomId == roomId).Published);

        // Act and assert: the report resolved, then deleted
        Report report = Assert.Single(await admin.GetReportsAsync(includeResolved: false, Ct));
        await admin.SetResolvedAsync(report.Id, true, Ct);
        Assert.Empty(await admin.GetReportsAsync(includeResolved: false, Ct));
        Assert.True(Assert.Single(await admin.GetReportsAsync(includeResolved: true, Ct)).Resolved);
        await admin.DeleteReportAsync(report.Id, Ct);
        // Done
        Assert.Empty(await admin.GetReportsAsync(includeResolved: true, Ct));
    }

    /// <summary>Over the network, only a server admin may manage, sees no files, and the manager's session is logged out when it closes.</summary>
    [Fact]
    public async Task ManageOverNetwork()
    {
        // Arrange: an admin and a user, made through the socket
        await using SocketHomeserver server = await SocketHomeserver.StartAsync();
        await using (HttpServerAdmin local = await HttpServerAdmin.ConnectSocketAsync(server.Socket, Ct))
        {
            await local.CreateUserAsync("alice", "password", admin: true, Ct);
            await local.CreateUserAsync("bob", "password", admin: false, Ct);
        }

        // Act and assert: refused for the user
        MatrixException refused = await Assert.ThrowsAsync<MatrixException>(() => HttpServerAdmin.LoginAsync(server.Address, "bob", "password", Ct));
        Assert.Equal(ErrorCodes.Forbidden, refused.Errcode);

        // Act and assert: the admin manages, without files
        HttpServerAdmin admin = await HttpServerAdmin.LoginAsync(server.Address, "alice", "password", Ct);
        ServerSnapshot snapshot = await admin.GetSnapshotAsync(Ct);
        Assert.Equal(SocketHomeserver.ServerName, snapshot.ServerName);
        Assert.Null(snapshot.SettingsFile);
        Assert.Equal(2, (await admin.GetUsersAsync(Ct)).Count);
        await admin.DisposeAsync();

        // Assert: one device left, the user's own login above was refused before it counted
        AdminUser alice;
        await using (HttpServerAdmin local = await HttpServerAdmin.ConnectSocketAsync(server.Socket, Ct)) alice = (await local.GetUsersAsync(Ct)).Single(u => u.UserId == "@alice:test.local");
        // Done
        Assert.Equal(0, alice.Devices);
    }

    /// <summary>Through the socket, the manager reads the server's settings with their sources, applies a change live and saves it to the server's file; secrets stay hidden and cannot be changed, invalid values are refused, and the network cannot reach any of it.</summary>
    [Fact]
    public async Task ManageSettingsThroughSocket()
    {
        // Arrange: the editor over the socket
        await using SocketHomeserver server = await SocketHomeserver.StartAsync();
        await using HttpServerAdmin admin = await HttpServerAdmin.ConnectSocketAsync(server.Socket, Ct);
        SettingsEditor editor = SocketSettingsStore.Open(admin);
        OptionInfo registration = editor.Options.Single(o => o.Key == "Server:Registration");
        OptionInfo secret = editor.Options.Single(o => o.Key == "Calls:TurnSharedSecret");

        // Assert: the server's file, its values and sources, the secret hidden
        Assert.Equal(server.SettingsFile, editor.SettingsPath);
        Assert.Equal(SocketHomeserver.ServerName, editor.GetValue(editor.Options.Single(o => o.Key == "Server:ServerName")));
        Assert.Equal("(hidden)", editor.GetDisplayValue(secret));

        // Act and assert: applied live, then saved to the file by the server
        Assert.Null(editor.Set(registration, "Token"));
        editor.Apply();
        SettingsEditor reread = SocketSettingsStore.Open(admin);
        Assert.Equal("Token", reread.GetValue(registration));
        Assert.Equal("applied", reread.GetSource(registration));
        editor.Save();
        Assert.Equal("Token", SettingsStore.Read(server.SettingsFile)["Server:Registration"]);
        Assert.Equal(Path.GetFileName(server.SettingsFile), SocketSettingsStore.Open(admin).GetSource(registration));

        // Act and assert: refused changes
        IOException invalid = await Assert.ThrowsAsync<IOException>(() => admin.ChangeSettingsAsync("apply", new Dictionary<string, string?> { ["Server:Registration"] = "Anyone" }, Ct));
        Assert.Contains("Server:Registration", invalid.Message);
        await Assert.ThrowsAsync<IOException>(() => admin.ChangeSettingsAsync("save", new Dictionary<string, string?> { ["Calls:TurnSharedSecret"] = "mine" }, Ct));
        await Assert.ThrowsAsync<IOException>(() => admin.ChangeSettingsAsync("apply", new Dictionary<string, string?> { ["No:Such"] = "x" }, Ct));

        // Assert: nothing of it over the network
        using HttpClient network = new() { BaseAddress = server.Address };
        using HttpResponseMessage response = await network.GetAsync("_uniform/admin/v1/settings", Ct);
        // Done
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
