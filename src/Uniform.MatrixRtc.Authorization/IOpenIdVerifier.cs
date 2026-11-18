namespace Uniform.LiveKit.Authorization;

using Uniform.LiveKit.Protocol;

/// <summary>Verifies a Matrix OpenID token with the homeserver that issued it.</summary>
public interface IOpenIdVerifier
{
    /// <summary>Return the user the homeserver says <paramref name="token"/> is of, or null when it refuses the token; throw when the homeserver cannot be asked.</summary>
    Task<string?> VerifyAsync(OpenIdToken token, CancellationToken cancellationToken);
}
