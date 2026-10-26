namespace Uniform.Matrix.Server.Accounts;

/// <summary>The authenticated user and device of a request.</summary>
/// <param name="UserId">Full user ID.</param>
/// <param name="DeviceId">Device of the access token.</param>
public sealed record Requester(string UserId, string DeviceId);
