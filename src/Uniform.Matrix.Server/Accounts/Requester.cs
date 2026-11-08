namespace Uniform.Matrix.Server.Accounts;

/// <summary>The authenticated user and device of a request.</summary>
/// <param name="UserId">Full user ID.</param>
/// <param name="DeviceId">Device of the access token.</param>
/// <param name="Admin">Whether the user administers the server.</param>
/// <param name="Suspended">Whether the account is suspended, so it may read but not send.</param>
/// <param name="Locked">Whether the account is locked, so only logging out is allowed.</param>
public sealed record Requester(string UserId, string DeviceId, bool Admin = false, bool Suspended = false, bool Locked = false);
