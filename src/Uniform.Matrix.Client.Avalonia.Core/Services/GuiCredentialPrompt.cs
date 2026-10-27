namespace Uniform.Matrix.Client.Avalonia.Core.Services;

using Uniform.Matrix.Client.Services;

/// <summary>Credential prompt with the password from the sign-in form; without one, the engine's request for it ends the start, so the form can ask.</summary>
internal sealed class GuiCredentialPrompt(string? password) : ICredentialPrompt
{
    /// <summary>Return the password given at sign-in; throw <see cref="PasswordRequiredException"/> without one.</summary>
    public Task<string> GetPasswordAsync(string userId, CancellationToken cancellationToken) =>
        password is null ? throw new PasswordRequiredException($"Sign in to {userId} with your password.") : Task.FromResult(password);
}

/// <summary>The engine needs a password, as no stored session could be resumed.</summary>
public sealed class PasswordRequiredException(string message) : Exception(message);
