namespace Uniform.Matrix.Server.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>How fast clients may write and log in, configuration section <see cref="Section"/>; requests beyond the limits get <c>M_LIMIT_EXCEEDED</c> with the time to wait, which clients retry after.</summary>
/// <remarks>Each limit is a token bucket: a burst at once, then a steady rate. Writes are counted per access token, logins and registrations per client address.</remarks>
public sealed record RateLimitOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "RateLimits";

    /// <summary>Whether the limits apply.</summary>
    [RestartRequired]
    public bool Enabled { get; set; } = true;
    /// <summary>Writes, such as messages, state changes, invites, new rooms and uploads, allowed at once.</summary>
    [RestartRequired]
    public int WriteBurst { get; set; } = 50;
    /// <summary>Writes allowed per second after the burst.</summary>
    [RestartRequired]
    public int WritesPerSecond { get; set; } = 10;
    /// <summary>Logins and registrations from one address allowed at once.</summary>
    [RestartRequired]
    public int LoginBurst { get; set; } = 20;
    /// <summary>Logins and registrations from one address allowed per minute after the burst.</summary>
    [RestartRequired]
    public int LoginsPerMinute { get; set; } = 10;
}
