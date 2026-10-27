namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;

/// <summary>Registration, <see href="https://spec.matrix.org/latest/client-server-api/#login">login</see>, logout, whoami, password change and capabilities.</summary>
public static class AccountEndpoints
{
    /// <summary>Map the account endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapAccounts(this IEndpointRouteBuilder routes)
    {
        // Login
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3");
        client.MapGet("/login", () => MatrixHttp.Json(new LoginFlowsResponse { Flows = [new LoginFlow { Type = AuthTypes.Password }] }));
        client.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimits.Logins);

        // Registration
        client.MapPost("/register", RegisterAsync).RequireRateLimiting(RateLimits.Logins);
        client.MapGet("/register/available", async (string username, AccountService accounts, CancellationToken cancellationToken) => await accounts.IsAvailableAsync(username, cancellationToken)
            ? MatrixHttp.Json(new JsonObject { ["available"] = true })
            : MatrixHttp.Error(HttpStatusCode.BadRequest, ErrorCodes.UserInUse, "The user ID is already taken."));
        client.MapGet("/register/m.login.registration_token/validity", (string token, IOptionsMonitor<ServerOptions> options) =>
            MatrixHttp.Json(new JsonObject { ["valid"] = options.CurrentValue.Registration == RegistrationMode.Token && IsRegistrationToken(options.CurrentValue, token) }));

        // Session
        client.MapGet("/account/whoami", (HttpContext http) => MatrixHttp.Json(new WhoAmIResponse { UserId = http.GetRequester().UserId, DeviceId = http.GetRequester().DeviceId, IsGuest = false })).RequireAccessToken();
        client.MapPost("/logout", async (HttpContext http, AccountService accounts, CancellationToken cancellationToken) => { await accounts.LogoutAsync(http.GetRequester(), cancellationToken); return MatrixHttp.Json(new JsonObject()); }).RequireAccessToken();
        client.MapPost("/logout/all", async (HttpContext http, AccountService accounts, CancellationToken cancellationToken) => { await accounts.LogoutAllAsync(http.GetRequester().UserId, cancellationToken); return MatrixHttp.Json(new JsonObject()); }).RequireAccessToken();
        client.MapPost("/account/password", ChangePasswordAsync).RequireAccessToken();

        // What clients may do here
        client.MapGet("/capabilities", () => MatrixHttp.Json(new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["m.change_password"] = new JsonObject { ["enabled"] = true },
                ["m.set_displayname"] = new JsonObject { ["enabled"] = true },
                ["m.set_avatar_url"] = new JsonObject { ["enabled"] = true },
                ["m.3pid_changes"] = new JsonObject { ["enabled"] = false },
                ["m.room_versions"] = new JsonObject { ["default"] = "10", ["available"] = new JsonObject { ["10"] = "stable", ["11"] = "stable" } },
            },
        })).RequireAccessToken();
        // Return routes
        return routes;
    }

    /// <summary>Password login with an <c>m.id.user</c> identifier or the deprecated <c>user</c> field.</summary>
    private static async Task<IResult> LoginAsync(JsonObject body, AccountService accounts, CancellationToken cancellationToken)
    {
        // Password logins only
        if (body["type"]?.GetValue<string>() != AuthTypes.Password) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, "Only password login is supported.");
        string user = body["identifier"]?["type"]?.GetValue<string>() is "m.id.user" ? body["identifier"]!["user"]?.GetValue<string>() ?? "" : body["user"]?.GetValue<string>() ?? "";
        string password = body["password"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing password.");

        // Log in
        LoginResponse response = await accounts.LoginAsync(user, password, body["device_id"]?.GetValue<string>(), body["initial_device_display_name"]?.GetValue<string>(), cancellationToken);
        // Return session
        return MatrixHttp.Json(response);
    }

    /// <summary>Register a user account after the configured auth stage; guests are not supported.</summary>
    private static async Task<IResult> RegisterAsync(RegisterRequest body, string? kind, AccountService accounts, UserInteractiveAuth auth, IOptionsMonitor<ServerOptions> options, CancellationToken cancellationToken)
    {
        // Assert allowed
        ServerOptions server = options.CurrentValue;
        if (kind == "guest") throw new MatrixException(HttpStatusCode.Forbidden, "M_GUEST_ACCESS_FORBIDDEN", "Guest access is not allowed.");
        if (server.Registration == RegistrationMode.Disabled) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Registration is disabled.");
        if (body.Password is null) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing password.");
        if (body.Username is not null && !await accounts.IsAvailableAsync(body.Username, cancellationToken)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UserInUse, "The user ID is already taken.");

        // Dummy stage for open registration, a token otherwise
        string stage = server.Registration == RegistrationMode.Open ? AuthTypes.Dummy : AuthTypes.RegistrationToken;
        await auth.AuthenticateAsync(body.Auth, [[stage]], (type, data, _) => Task.FromResult(type == AuthTypes.Dummy || IsRegistrationToken(server, data["token"]?.GetValue<string>())), cancellationToken);

        // Create
        RegisterResponse response = await accounts.RegisterAsync(body.Username, body.Password, body.DeviceId, body.InitialDeviceDisplayName, body.InhibitLogin == true, admin: false, cancellationToken);
        // Return account
        return MatrixHttp.Json(response);
    }

    /// <summary>Change the password after password auth; log out other devices unless asked not to.</summary>
    private static async Task<IResult> ChangePasswordAsync(HttpContext http, JsonObject body, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken)
    {
        // Assert new password
        Requester requester = http.GetRequester();
        string newPassword = body["new_password"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing new_password.");

        // Current password, then change
        await RequirePasswordAsync(requester, body["auth"] as JsonObject, accounts, auth, cancellationToken);
        bool logoutOthers = body["logout_devices"]?.GetValue<bool>() ?? true;
        await accounts.ChangePasswordAsync(requester.UserId, newPassword, logoutOthers ? requester.DeviceId : null, cancellationToken);
        // Return empty
        return MatrixHttp.Json(new JsonObject());
    }

    /// <summary>Require a password stage for the requester's own account.</summary>
    internal static Task RequirePasswordAsync(Requester requester, JsonObject? authData, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken) =>
        auth.AuthenticateAsync(authData, [[AuthTypes.Password]], async (_, data, token) =>
        {
            // Identifier must be the requester
            string? user = data["identifier"]?["user"]?.GetValue<string>() ?? data["user"]?.GetValue<string>();
            string? password = data["password"]?.GetValue<string>();
            if (user is null || password is null) return false;
            bool self = user == requester.UserId || $"@{user}:{accounts.ServerName}" == requester.UserId;
            // Return verification
            return self && await accounts.CheckPasswordAsync(requester.UserId, password, token);
        }, cancellationToken);

    /// <summary>Return whether <paramref name="token"/> is a configured registration token, compared in constant time.</summary>
    private static bool IsRegistrationToken(ServerOptions server, string? token)
    {
        // Compare against each configured token
        if (token is null) return false;
        byte[] given = Encoding.UTF8.GetBytes(token);
        // Return match
        return server.GetRegistrationTokens().Any(t => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t), given));
    }
}
