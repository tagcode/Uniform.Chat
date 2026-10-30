namespace Uniform.Matrix.Client.Avalonia.Core.Services;

using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Create accounts for the sign-in form.</summary>
public interface IAccountCreator
{
    /// <summary>Return what registering <see cref="SignIn.UserId"/> on its server needs; throw <see cref="MatrixException"/> when the server does not allow it.</summary>
    Task<RegistrationRequirements> GetRequirementsAsync(SignIn signIn);

    /// <summary>Register <see cref="SignIn.UserId"/> with <see cref="SignIn.Password"/>, without signing in.</summary>
    Task CreateAsync(SignIn signIn, string? registrationToken, bool acceptTerms);
}

/// <summary>Create accounts over HTTP with <see cref="AccountRegistration"/>.</summary>
/// <param name="handler">HTTP handler; null for the default.</param>
public sealed class AccountCreator(HttpMessageHandler? handler = null) : IAccountCreator
{
    /// <summary>One client for the few requests of registering.</summary>
    private readonly HttpClient http = handler is null ? new() { Timeout = TimeSpan.FromSeconds(30) } : new(handler) { Timeout = TimeSpan.FromSeconds(30) };

    /// <inheritdoc />
    public async Task<RegistrationRequirements> GetRequirementsAsync(SignIn signIn)
    {
        // Server, then its stages
        Uri homeserver = await AccountRegistration.FindHomeserverAsync(http, UserId.Parse(signIn.UserId), signIn.Homeserver);
        // Return requirements
        return await AccountRegistration.GetRequirementsAsync(http, homeserver);
    }

    /// <inheritdoc />
    public async Task CreateAsync(SignIn signIn, string? registrationToken, bool acceptTerms)
    {
        // Server, then the account
        UserId userId = UserId.Parse(signIn.UserId);
        Uri homeserver = await AccountRegistration.FindHomeserverAsync(http, userId, signIn.Homeserver);
        await AccountRegistration.RegisterAsync(http, homeserver, userId, signIn.Password ?? "", registrationToken, acceptTerms);
    }
}
