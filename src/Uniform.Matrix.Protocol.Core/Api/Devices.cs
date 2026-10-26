namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Nodes;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3devices">GET /_matrix/client/v3/devices</see>.</summary>
public sealed record DevicesResponse
{
    /// <summary>Devices of the user.</summary>
    public IReadOnlyList<Device> Devices { get; init; } = [];
}

/// <summary>A device, i.e. one logged-in session, of the user.</summary>
public sealed record Device
{
    /// <summary>Device ID.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Display name, e.g. the client name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>IP address the device was last seen from.</summary>
    public string? LastSeenIp { get; init; }
    /// <summary>Last activity in milliseconds since Unix epoch.</summary>
    public long? LastSeenTs { get; init; }
}

/// <summary>401 body of the <see href="https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api">user-interactive auth</see> API.</summary>
public sealed record UserInteractiveAuthResponse
{
    /// <summary>Accepted combinations of stages.</summary>
    public IReadOnlyList<AuthFlow> Flows { get; init; } = [];
    /// <summary>Parameters of each stage, by stage type; servers send an empty object when no stage needs any.</summary>
    public IReadOnlyDictionary<string, JsonObject>? Params { get; init; }
    /// <summary>Session to pass back in the next attempt.</summary>
    public string? Session { get; init; }
    /// <summary>Stages already completed in this session.</summary>
    public IReadOnlyList<string> Completed { get; init; } = [];
    /// <summary>Error of the previous attempt, e.g. <c>M_FORBIDDEN</c> for a wrong password.</summary>
    public string? Errcode { get; init; }
    /// <summary>Human-readable error of the previous attempt.</summary>
    public string? Error { get; init; }
}

/// <summary>One accepted combination of auth stages.</summary>
public sealed record AuthFlow
{
    /// <summary>Stage types to complete in order, see <see cref="AuthTypes"/>.</summary>
    public IReadOnlyList<string> Stages { get; init; } = [];
}

/// <summary>Common <see href="https://spec.matrix.org/latest/client-server-api/#authentication-types">authentication types</see>.</summary>
public static class AuthTypes
{
    /// <summary>Password of the user.</summary>
    public const string Password = "m.login.password";
    /// <summary>Single sign-on, completed in a browser.</summary>
    public const string Sso = "m.login.sso";
    /// <summary>Terms of service acceptance.</summary>
    public const string Terms = "m.login.terms";
    /// <summary>No input; completes at once, e.g. for open registration.</summary>
    public const string Dummy = "m.login.dummy";
    /// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#token-authenticated-registration">registration token</see> given by the server's admin.</summary>
    public const string RegistrationToken = "m.login.registration_token";
}
