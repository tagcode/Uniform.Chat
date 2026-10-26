namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Nodes;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3login">GET /_matrix/client/v3/login</see>.</summary>
public sealed record LoginFlowsResponse
{
    /// <summary>Login types the server accepts.</summary>
    public IReadOnlyList<LoginFlow> Flows { get; init; } = [];
}

/// <summary>One supported login type, e.g. <c>m.login.password</c>.</summary>
public sealed record LoginFlow
{
    /// <summary>Login type.</summary>
    public required string Type { get; init; }
}

/// <summary>Request of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3login">POST /_matrix/client/v3/login</see> with type <c>m.login.password</c>.</summary>
public sealed record PasswordLoginRequest
{
    /// <summary>Login type, always <c>m.login.password</c>.</summary>
    public string Type { get; init; } = "m.login.password";
    /// <summary>User to log in as.</summary>
    public required UserIdentifier Identifier { get; init; }
    /// <summary>Password of the user.</summary>
    public required string Password { get; init; }
    /// <summary>Display name for a newly created device.</summary>
    public string? InitialDeviceDisplayName { get; init; }
    /// <summary>Existing device to reuse; server generates one when null.</summary>
    public string? DeviceId { get; init; }
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#identifier-types">Identifier</see> of type <c>m.id.user</c>.</summary>
public sealed record UserIdentifier
{
    /// <summary>Identifier type, always <c>m.id.user</c>.</summary>
    public string Type { get; init; } = "m.id.user";
    /// <summary>Localpart or full user ID.</summary>
    public required string User { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3login">POST /_matrix/client/v3/login</see>.</summary>
public sealed record LoginResponse
{
    /// <summary>Full user ID, e.g. <c>@alice:matrix.org</c>.</summary>
    public required string UserId { get; init; }
    /// <summary>Token for authenticated requests.</summary>
    public required string AccessToken { get; init; }
    /// <summary>Device the token belongs to.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Token for refreshing an expiring access token.</summary>
    public string? RefreshToken { get; init; }
    /// <summary>Access token lifetime in milliseconds; null when it does not expire.</summary>
    public long? ExpiresInMs { get; init; }
}

/// <summary>Request of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3register">POST /_matrix/client/v3/register</see>.</summary>
public sealed record RegisterRequest
{
    /// <summary>Wanted localpart; the server picks one when null.</summary>
    public string? Username { get; init; }
    /// <summary>Password of the new account.</summary>
    public string? Password { get; init; }
    /// <summary>Device to create; the server generates one when null.</summary>
    public string? DeviceId { get; init; }
    /// <summary>Display name of the new device.</summary>
    public string? InitialDeviceDisplayName { get; init; }
    /// <summary>Whether to skip logging in, so no access token is returned.</summary>
    public bool? InhibitLogin { get; init; }
    /// <summary>User-interactive auth of this attempt, e.g. <c>{"type":"m.login.dummy","session":"..."}</c>.</summary>
    public JsonObject? Auth { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3register">POST /_matrix/client/v3/register</see>; tokens are missing when login was inhibited.</summary>
public sealed record RegisterResponse
{
    /// <summary>Full user ID of the new account.</summary>
    public required string UserId { get; init; }
    /// <summary>Token for authenticated requests.</summary>
    public string? AccessToken { get; init; }
    /// <summary>Device the token belongs to.</summary>
    public string? DeviceId { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3accountwhoami">GET /_matrix/client/v3/account/whoami</see>.</summary>
public sealed record WhoAmIResponse
{
    /// <summary>Full user ID owning the token.</summary>
    public required string UserId { get; init; }
    /// <summary>Device owning the token.</summary>
    public string? DeviceId { get; init; }
    /// <summary>Whether the user is a guest.</summary>
    public bool? IsGuest { get; init; }
}
