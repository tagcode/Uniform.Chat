namespace Uniform.Matrix.Server.Tests;

using System.Net;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of registration, login, sessions, devices and password change, through our own client.</summary>
public class AccountTests
{
    private const string Password = "correct horse battery";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };

    /// <summary>Open registration logs the new user in; the name is then taken and logins work.</summary>
    [Fact]
    public async Task RegisterAndLogIn()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = server.CreateClient();

        // Act
        RegisterResponse registered = await alice.RegisterAsync("Alice", Password, deviceDisplayName: "phone", cancellationToken: Ct);
        WhoAmIResponse me = await alice.WhoAmIAsync(Ct);
        MatrixClient again = server.CreateClient();
        LoginResponse login = await again.LoginWithPasswordAsync("alice", Password, cancellationToken: Ct);

        // Assert
        Assert.Equal($"@alice:{TestHomeserver.ServerName}", registered.UserId);
        Assert.Equal(registered.DeviceId, me.DeviceId);
        Assert.False(await server.CreateClient().IsUsernameAvailableAsync("alice", Ct));
        Assert.True(await server.CreateClient().IsUsernameAvailableAsync("bob", Ct));
        Assert.Equal(registered.UserId, login.UserId);
        Assert.NotEqual(registered.DeviceId, login.DeviceId);
        MatrixException taken = await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().RegisterAsync("alice", Password, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.UserInUse, taken.Errcode);
    }

    /// <summary>Registration is off by default, and token registration needs a configured token.</summary>
    [Fact]
    public async Task RestrictRegistration()
    {
        // Arrange
        await using TestHomeserver closed = await TestHomeserver.StartAsync();
        await using TestHomeserver token = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Token", ["Server:RegistrationTokens"] = "first, second" });

        // Act and assert: closed
        MatrixException forbidden = await Assert.ThrowsAsync<MatrixException>(() => closed.CreateClient().RegisterAsync("alice", Password, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Errcode);

        // Act and assert: wrong token keeps asking, right one registers
        await Assert.ThrowsAsync<UserInteractiveAuthException>(() => token.CreateClient().RegisterAsync("alice", Password, cancellationToken: Ct));
        await Assert.ThrowsAsync<UserInteractiveAuthException>(() => token.CreateClient().RegisterAsync("alice", Password, "third", cancellationToken: Ct));
        RegisterResponse registered = await token.CreateClient().RegisterAsync("alice", Password, "second", cancellationToken: Ct);
        Assert.NotNull(registered.AccessToken);
    }

    /// <summary>Wrong passwords, invalid names and unknown tokens are refused with the right error codes.</summary>
    [Fact]
    public async Task RefuseBadCredentials()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        await server.CreateClient().RegisterAsync("alice", Password, cancellationToken: Ct);

        // Act
        MatrixException wrong = await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync("alice", "nope", cancellationToken: Ct));
        MatrixException unknownUser = await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync("nobody", Password, cancellationToken: Ct));
        MatrixException invalid = await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().RegisterAsync("al ice", Password, cancellationToken: Ct));
        MatrixClient stale = new(server.CreateHttpClient(), new MatrixSession { Homeserver = server.BaseAddress, UserId = "@alice:test.local", DeviceId = "X", AccessToken = "mxt_forged" });
        MatrixException unknownToken = await Assert.ThrowsAsync<MatrixException>(() => stale.WhoAmIAsync(Ct));
        HttpResponseMessage missing = await server.CreateHttpClient().GetAsync("/_matrix/client/v3/account/whoami", Ct);

        // Assert
        Assert.Equal(ErrorCodes.Forbidden, wrong.Errcode);
        Assert.Equal(wrong.Error, unknownUser.Error);
        Assert.Equal(ErrorCodes.InvalidUsername, invalid.Errcode);
        Assert.Equal(ErrorCodes.UnknownToken, unknownToken.Errcode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains(ErrorCodes.MissingToken, await missing.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Devices are listed, renamed and deleted with the password; deleting a device ends its session.</summary>
    [Fact]
    public async Task ManageDevices()
    {
        // Arrange: two sessions of alice
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient first = server.CreateClient();
        await first.RegisterAsync("alice", Password, deviceDisplayName: "first", cancellationToken: Ct);
        MatrixClient second = server.CreateClient();
        LoginResponse other = await second.LoginWithPasswordAsync("alice", Password, "second", Ct);

        // Act: list and rename
        IReadOnlyList<Device> devices = await first.GetDevicesAsync(Ct);
        await first.RenameDeviceAsync(other.DeviceId, "renamed", Ct);

        // Assert
        Assert.Equal(2, devices.Count);
        Assert.Contains(await first.GetDevicesAsync(Ct), d => d.DeviceId == other.DeviceId && d.DisplayName == "renamed");

        // Act and assert: deleting needs the password, then the session is gone
        await Assert.ThrowsAsync<UserInteractiveAuthException>(() => first.DeleteDevicesAsync([other.DeviceId], password: null, Ct));
        await Assert.ThrowsAsync<UserInteractiveAuthException>(() => first.DeleteDevicesAsync([other.DeviceId], "wrong", Ct));
        await first.DeleteDevicesAsync([other.DeviceId], Password, Ct);
        MatrixException ended = await Assert.ThrowsAsync<MatrixException>(() => second.WhoAmIAsync(Ct));
        Assert.Equal(ErrorCodes.UnknownToken, ended.Errcode);
        Assert.Single(await first.GetDevicesAsync(Ct));
    }

    /// <summary>Logout ends one session, logout all ends every one, and a password change logs out the other devices.</summary>
    [Fact]
    public async Task EndSessions()
    {
        // Arrange: three sessions
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient a = server.CreateClient();
        await a.RegisterAsync("alice", Password, cancellationToken: Ct);
        MatrixClient b = server.CreateClient();
        await b.LoginWithPasswordAsync("alice", Password, cancellationToken: Ct);
        MatrixClient c = server.CreateClient();
        await c.LoginWithPasswordAsync("alice", Password, cancellationToken: Ct);

        // Act and assert: password change keeps only this device
        await a.ChangePasswordAsync(Password, "new password", cancellationToken: Ct);
        await Assert.ThrowsAsync<MatrixException>(() => b.WhoAmIAsync(Ct));
        await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync("alice", Password, cancellationToken: Ct));
        Assert.Single(await a.GetDevicesAsync(Ct));

        // Act and assert: logout all
        MatrixClient d = server.CreateClient();
        await d.LoginWithPasswordAsync("alice", "new password", cancellationToken: Ct);
        await a.LogoutAllAsync(Ct);
        await Assert.ThrowsAsync<MatrixException>(() => d.WhoAmIAsync(Ct));
        Assert.Null(a.Session);
    }
}
