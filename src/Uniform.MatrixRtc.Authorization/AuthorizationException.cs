namespace Uniform.LiveKit.Authorization;

using System.Net;
using Uniform.LiveKit.Protocol;

/// <summary>A refused request, with the HTTP status and Matrix error code to answer with.</summary>
/// <param name="status">The HTTP status, e.g. 403.</param>
/// <param name="errorCode">The error code, e.g. <see cref="AuthorizationError.Forbidden"/>.</param>
/// <param name="message">The reason.</param>
public sealed class AuthorizationException(HttpStatusCode status, string errorCode, string message) : Exception(message)
{
    /// <summary>The HTTP status.</summary>
    public HttpStatusCode Status { get; } = status;
    /// <summary>The Matrix error code.</summary>
    public string ErrorCode { get; } = errorCode;

    /// <summary>Return the body to answer with.</summary>
    public AuthorizationError ToError() => new() { ErrorCode = ErrorCode, Error = Message };
}
