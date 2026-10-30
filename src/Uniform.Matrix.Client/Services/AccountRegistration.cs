namespace Uniform.Matrix.Client.Services;

using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Create an account before the first sign-in: find the server, learn what registering needs there, and register without logging in, so the apps then sign in the usual way.</summary>
public static class AccountRegistration
{
    /// <summary>Return the address of the server of <paramref name="userId"/>: <paramref name="homeserver"/> when given, else the one its <c>.well-known</c> names.</summary>
    public static async Task<Uri> FindHomeserverAsync(HttpClient http, UserId userId, string? homeserver, CancellationToken cancellationToken = default)
    {
        // Given address, else discovery
        if (homeserver is { Length: > 0 } given) return new Uri(given.EndsWith('/') ? given : given + "/");
        // Return discovered
        return await HomeserverDiscovery.DiscoverAsync(http, userId.ServerName, cancellationToken);
    }

    /// <summary>Return what registering on <paramref name="homeserver"/> needs; throw <see cref="MatrixException"/> when it does not allow registering.</summary>
    public static Task<RegistrationRequirements> GetRequirementsAsync(HttpClient http, Uri homeserver, CancellationToken cancellationToken = default) =>
        new MatrixClient(http, homeserver).GetRegistrationRequirementsAsync(cancellationToken);

    /// <summary>Register <paramref name="userId"/> on <paramref name="homeserver"/> without logging in; throw <see cref="MatrixException"/> when the name is taken or invalid, and <see cref="UserInteractiveAuthException"/> when the token is wrong or the terms were not accepted.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="homeserver">Server address, from <see cref="FindHomeserverAsync"/>.</param>
    /// <param name="userId">Wanted user ID; its localpart becomes the account name.</param>
    /// <param name="password">Password of the new account.</param>
    /// <param name="registrationToken">Token from the server's admin, when <see cref="RegistrationRequirements.NeedsToken"/>.</param>
    /// <param name="acceptTerms">Whether the user accepted <see cref="RegistrationRequirements.Policies"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task RegisterAsync(HttpClient http, Uri homeserver, UserId userId, string password, string? registrationToken, bool acceptTerms, CancellationToken cancellationToken = default)
    {
        // The localpart is the name; the server adds its own name
        MatrixClient client = new(http, homeserver);
        RegisterResponse response = await client.RegisterAsync(userId.Localpart, password, registrationToken is { Length: > 0 } ? registrationToken : null, acceptTerms: acceptTerms, inhibitLogin: true, cancellationToken: cancellationToken);
        // On a server of another name than the user ID's, the account is not the one asked for
        if (response.UserId != userId.ToString()) throw new MatrixException(System.Net.HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"The server created {response.UserId}, not {userId}; check the server address.");
    }
}
