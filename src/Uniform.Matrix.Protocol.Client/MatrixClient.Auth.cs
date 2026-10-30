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

    /// <summary>Return what <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3register">registering</see> on the server needs: whether it is open at all, a registration token, and policies to accept; throw <see cref="MatrixException"/> when registration is disabled.</summary>
    public async Task<RegistrationRequirements> GetRegistrationRequirementsAsync(CancellationToken cancellationToken = default)
    {
        // A request without auth is answered with the flows
        try
        {
            await SendAsync(HttpMethod.Post, "_matrix/client/v3/register", new { }, authenticated: false, cancellationToken);
        }
        catch (UserInteractiveAuthException ex)
        {
            // The first flow of stages this client can answer
            AuthFlow? flow = ex.Response.Flows.FirstOrDefault(f => f.Stages.All(IsAnswerableRegistrationStage));
            // Return requirements
            return new RegistrationRequirements
            {
                Supported = flow is not null,
                NeedsToken = flow?.Stages.Contains(AuthTypes.RegistrationToken) == true,
                Policies = flow?.Stages.Contains(AuthTypes.Terms) == true ? TermsPolicy.From(ex.Response) : [],
            };
        }
        // On a server that registered without any stage, which a later request will too
        return new RegistrationRequirements { Supported = true };
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3register">Registers</see> an account and stores the <see cref="Session"/>; answers the stages of the first flow it can: the dummy stage of open registration, the token stage with <paramref name="registrationToken"/>, and the terms stage when <paramref name="acceptTerms"/>.</summary>
    /// <param name="username">Wanted localpart, e.g. <c>alice</c>.</param>
    /// <param name="password">Password of the new account.</param>
    /// <param name="registrationToken">Token from the server's admin, when registration needs one.</param>
    /// <param name="deviceDisplayName">Display name of the new device.</param>
    /// <param name="acceptTerms">Whether the user accepted the policies of <see cref="GetRegistrationRequirementsAsync"/>.</param>
    /// <param name="inhibitLogin">Whether to only create the account, without a device or <see cref="Session"/>, e.g. to log in afterwards the usual way.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Throws <see cref="UserInteractiveAuthException"/> when the server wants a stage this method cannot answer, e.g. a captcha, a wrong token, or terms not accepted.</remarks>
    public async Task<RegisterResponse> RegisterAsync(string username, string password, string? registrationToken = null, string? deviceDisplayName = null, bool acceptTerms = false, bool inhibitLogin = false, CancellationToken cancellationToken = default)
    {
        // Each attempt answers the next stage, until the account is made or a stage cannot be answered
        RegisterRequest request = new() { Username = username, Password = password, InitialDeviceDisplayName = deviceDisplayName, InhibitLogin = inhibitLogin ? true : null };
        string? answered = null;
        RegisterResponse response;
        while (true)
        {
            try
            {
                response = await SendAsync<RegisterResponse>(HttpMethod.Post, "_matrix/client/v3/register", request, authenticated: false, cancellationToken);
                break;
            }
            catch (UserInteractiveAuthException ex) when (NextRegistrationStage(ex.Response, registrationToken, acceptTerms) is JsonObject auth && !(ex.Response.Error is not null && auth["type"]?.GetValue<string>() == answered))
            {
                // The stage again only when it was not just refused
                answered = auth["type"]?.GetValue<string>();
                auth["session"] = ex.Response.Session;
                request = request with { Auth = auth };
            }
        }

        // Store session when logged in
        if (response.AccessToken is string token && response.DeviceId is string device) Session = new MatrixSession { Homeserver = Homeserver, UserId = response.UserId, DeviceId = device, AccessToken = token };
        // Return response
        return response;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3accountdeactivate">Deactivates</see> the account for good, confirming with <paramref name="password"/>, and clears <see cref="Session"/>; the user ID cannot be registered again.</summary>
    public async Task DeactivateAccountAsync(string password, CancellationToken cancellationToken = default)
    {
        // Password stage, then the session is gone
        await SendWithPasswordAuthAsync(HttpMethod.Post, "_matrix/client/v3/account/deactivate", auth => new { auth, erase = false }, password, cancellationToken);
        Session = null;
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

    /// <summary>Return whether this client can answer a registration stage when it has what the stage needs.</summary>
    private static bool IsAnswerableRegistrationStage(string stage) => stage is AuthTypes.Dummy or AuthTypes.RegistrationToken or AuthTypes.Terms;

    /// <summary>Return the <c>auth</c> object answering the next stage of the first registration flow this client can complete, or null when there is none.</summary>
    private static JsonObject? NextRegistrationStage(UserInteractiveAuthResponse response, string? registrationToken, bool acceptTerms)
    {
        // A flow of stages we can all answer
        bool CanAnswer(string stage) => stage == AuthTypes.Dummy || stage == AuthTypes.RegistrationToken && registrationToken is not null || stage == AuthTypes.Terms && acceptTerms;
        AuthFlow? flow = response.Flows.FirstOrDefault(f => f.Stages.All(CanAnswer));
        string? stage = flow?.Stages.FirstOrDefault(s => !response.Completed.Contains(s));
        // Return its next stage
        return stage switch
        {
            AuthTypes.RegistrationToken => new JsonObject { ["type"] = stage, ["token"] = registrationToken },
            null => null,
            _ => new JsonObject { ["type"] = stage },
        };
    }
}

/// <summary>What <see cref="MatrixClient.RegisterAsync"/> needs on a server, from <see cref="MatrixClient.GetRegistrationRequirementsAsync"/>.</summary>
public sealed record RegistrationRequirements
{
    /// <summary>Whether this client can register here; false when the server wants stages it cannot answer, e.g. a captcha or an email.</summary>
    public bool Supported { get; init; }
    /// <summary>Whether a registration token from the server's admin is needed.</summary>
    public bool NeedsToken { get; init; }
    /// <summary>Policies the user reads and accepts before registering; empty when there are none.</summary>
    public IReadOnlyList<TermsPolicy> Policies { get; init; } = [];
}
