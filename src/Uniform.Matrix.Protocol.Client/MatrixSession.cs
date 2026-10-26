namespace Uniform.Matrix.Protocol.Client;

/// <summary>Logged-in session; store it to resume with <see cref="MatrixClient(HttpClient, MatrixSession)"/> without logging in again.</summary>
public sealed record MatrixSession
{
    /// <summary>Homeserver base URL.</summary>
    public required Uri Homeserver { get; init; }
    /// <summary>Full user ID.</summary>
    public required string UserId { get; init; }
    /// <summary>Device ID.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Access token; keeps secret.</summary>
    public required string AccessToken { get; init; }
}
