namespace Uniform.Matrix.Protocol.Client;

using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Server info, login and logout.
public sealed partial class MatrixClient
{
    /// <summary>List <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientversions">spec versions</see> supported by the server.</summary>
    public Task<VersionsResponse> GetVersionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<VersionsResponse>(HttpMethod.Get, "_matrix/client/versions", body: null, authenticated: false, cancellationToken);

    /// <summary>List <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3login">login types</see> the server accepts.</summary>
    public Task<LoginFlowsResponse> GetLoginFlowsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<LoginFlowsResponse>(HttpMethod.Get, "_matrix/client/v3/login", body: null, authenticated: false, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3login">Logs in</see> with a password and stores the <see cref="Session"/>.</summary>
    /// <param name="user">Localpart or full user ID.</param>
    /// <param name="password">Password of the user.</param>
    /// <param name="deviceDisplayName">Display name for the new device; server default when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LoginResponse> LoginWithPasswordAsync(string user, string password, string? deviceDisplayName = null, CancellationToken cancellationToken = default)
    {
        // Send login request
        PasswordLoginRequest request = new() { Identifier = new UserIdentifier { User = user }, Password = password, InitialDeviceDisplayName = deviceDisplayName };
        LoginResponse response = await SendAsync<LoginResponse>(HttpMethod.Post, "_matrix/client/v3/login", request, authenticated: false, cancellationToken);
        // Store session
        Session = new MatrixSession { Homeserver = Homeserver, UserId = response.UserId, DeviceId = response.DeviceId, AccessToken = response.AccessToken };
        // Return response
        return response;
    }

    /// <summary>Return the user and device that <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3accountwhoami">own the access token</see>.</summary>
    public Task<WhoAmIResponse> WhoAmIAsync(CancellationToken cancellationToken = default) =>
        SendAsync<WhoAmIResponse>(HttpMethod.Get, "_matrix/client/v3/account/whoami", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3logout">Invalidates the access token</see>, deletes the device and clears <see cref="Session"/>.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        // Send logout request
        await SendAsync(HttpMethod.Post, "_matrix/client/v3/logout", new { }, authenticated: true, cancellationToken);
        // Clear session
        Session = null;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3register">Registers</see> an account and stores the <see cref="Session"/>; answers the dummy stage of open registration, or the token stage with <paramref name="registrationToken"/>.</summary>
    /// <param name="username">Wanted localpart, e.g. <c>alice</c>.</param>
    /// <param name="password">Password of the new account.</param>
    /// <param name="registrationToken">Token from the server's admin, when registration needs one.</param>
    /// <param name="deviceDisplayName">Display name of the new device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Throws <see cref="UserInteractiveAuthException"/> when the server wants a stage this method cannot answer, e.g. a captcha.</remarks>
    public async Task<RegisterResponse> RegisterAsync(string username, string password, string? registrationToken = null, string? deviceDisplayName = null, CancellationToken cancellationToken = default)
    {
        // First attempt without auth returns the flows
        RegisterRequest request = new() { Username = username, Password = password, InitialDeviceDisplayName = deviceDisplayName };
        RegisterResponse response;
        try
        {
            response = await SendAsync<RegisterResponse>(HttpMethod.Post, "_matrix/client/v3/register", request, authenticated: false, cancellationToken);
        }
        catch (UserInteractiveAuthException ex) when (NextRegistrationStage(ex.Response, registrationToken) is JsonObject auth)
        {
            // Answer the stage once
            auth["session"] = ex.Response.Session;
            response = await SendAsync<RegisterResponse>(HttpMethod.Post, "_matrix/client/v3/register", request with { Auth = auth }, authenticated: false, cancellationToken);
        }

        // Store session when logged in
        if (response.AccessToken is string token && response.DeviceId is string device) Session = new MatrixSession { Homeserver = Homeserver, UserId = response.UserId, DeviceId = device, AccessToken = token };
        // Return response
        return response;
    }

    /// <summary>Return whether <paramref name="username"/> can be <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3registeravailable">registered</see>; throw <see cref="MatrixException"/> for invalid names.</summary>
    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken cancellationToken = default)
    {
        // Taken names are an error response
        try
        {
            await SendAsync(HttpMethod.Get, $"_matrix/client/v3/register/available?username={Uri.EscapeDataString(username)}", body: null, authenticated: false, cancellationToken);
            return true;
        }
        catch (MatrixException ex) when (ex.Errcode == ErrorCodes.UserInUse)
        {
            // Done; taken
            return false;
        }
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3logoutall">Logs out every device</see> of the user, including this one, and clears <see cref="Session"/>.</summary>
    public async Task LogoutAllAsync(CancellationToken cancellationToken = default)
    {
        // Send, then clear session
        await SendAsync(HttpMethod.Post, "_matrix/client/v3/logout/all", new { }, authenticated: true, cancellationToken);
        Session = null;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3accountpassword">Changes the password</see>, confirming with the current one; other devices are logged out unless <paramref name="logoutDevices"/> is false.</summary>
    public Task ChangePasswordAsync(string currentPassword, string newPassword, bool logoutDevices = true, CancellationToken cancellationToken = default) =>
        SendWithPasswordAuthAsync(HttpMethod.Post, "_matrix/client/v3/account/password", auth => new { new_password = newPassword, logout_devices = logoutDevices, auth }, currentPassword, cancellationToken);

    /// <summary>Return the <c>auth</c> object answering registration's next stage, or null when it is not one this client answers.</summary>
    private static JsonObject? NextRegistrationStage(UserInteractiveAuthResponse response, string? registrationToken)
    {
        // A single-stage flow we can complete
        foreach (AuthFlow flow in response.Flows)
        {
            if (flow.Stages is [AuthTypes.Dummy]) return new JsonObject { ["type"] = AuthTypes.Dummy };
            if (flow.Stages is [AuthTypes.RegistrationToken] && registrationToken is not null) return new JsonObject { ["type"] = AuthTypes.RegistrationToken, ["token"] = registrationToken };
        }
        // Done; nothing to answer
        return null;
    }
}
