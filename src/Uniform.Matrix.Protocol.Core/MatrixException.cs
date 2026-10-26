namespace Uniform.Matrix.Protocol.Core;

using System.Net;

/// <summary>A homeserver returned a <see href="https://spec.matrix.org/latest/client-server-api/#standard-error-response">Matrix error</see>, e.g. <c>M_FORBIDDEN</c> or <c>M_UNKNOWN_TOKEN</c>.</summary>
public sealed class MatrixException(HttpStatusCode statusCode, string errcode, string? error, TimeSpan? retryAfter = null)
    : Exception($"{errcode}: {error ?? statusCode.ToString()}{(retryAfter is TimeSpan wait ? $" (retry after {wait.TotalSeconds:F0} s)" : "")}")
{
    /// <summary>HTTP status of the response.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
    /// <summary>Matrix error code.</summary>
    public string Errcode { get; } = errcode;
    /// <summary>Human-readable error message.</summary>
    public string? Error { get; } = error;
    /// <summary>Wait time requested by the server with <c>M_LIMIT_EXCEEDED</c>.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
