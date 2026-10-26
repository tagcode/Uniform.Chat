namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

/// <summary>The server requires <see href="https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api">user-interactive authentication</see>, e.g. the password, before the request.</summary>
/// <remarks>Methods that support it take a password and answer the challenge themselves; this is thrown when no password was given or the server wants a stage the client does not support.</remarks>
public sealed class UserInteractiveAuthException(UserInteractiveAuthResponse response)
    : Exception(response.Error ?? $"Authentication required: {string.Join(" or ", response.Flows.Select(f => string.Join(" + ", f.Stages)))}")
{
    /// <summary>Flows the server accepts and the session to continue.</summary>
    public UserInteractiveAuthResponse Response { get; } = response;
    /// <summary>Whether a password alone completes one of the flows.</summary>
    public bool AcceptsPassword => Response.Flows.Any(f => f.Stages is [AuthTypes.Password]);
}
