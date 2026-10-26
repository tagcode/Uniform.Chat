namespace Uniform.Matrix.Client.Services;

/// <summary>Asks the user for a password; implemented by the active frontend.</summary>
public interface ICredentialPrompt
{
    /// <summary>Return the password of <paramref name="userId"/>, entered without echo.</summary>
    Task<string> GetPasswordAsync(string userId, CancellationToken cancellationToken);
}
