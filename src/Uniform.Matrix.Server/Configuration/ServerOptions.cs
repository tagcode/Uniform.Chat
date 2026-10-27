namespace Uniform.Matrix.Server.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>Who may create accounts, see <see cref="ServerOptions.Registration"/>.</summary>
public enum RegistrationMode
{
    /// <summary>Only an admin creates accounts.</summary>
    Disabled,
    /// <summary>Anyone may register.</summary>
    Open,
    /// <summary>Registering needs one of <see cref="ServerOptions.RegistrationTokens"/>.</summary>
    Token,
}

/// <summary>Homeserver identity, public address and registration, configuration section <see cref="Section"/>.</summary>
public sealed record ServerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Server";

    /// <summary>Server name in user IDs and room aliases, e.g. <c>example.org</c> in <c>@alice:example.org</c>; changing it later orphans every account.</summary>
    [RestartRequired]
    public string ServerName { get; set; } = "localhost";
    /// <summary>Public URL clients use, e.g. <c>https://matrix.example.org</c>, announced in <c>/.well-known/matrix/client</c>; none when unset.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Who may create accounts: <see cref="RegistrationMode.Disabled"/>, <see cref="RegistrationMode.Open"/> or <see cref="RegistrationMode.Token"/>.</summary>
    public RegistrationMode Registration { get; set; } = RegistrationMode.Disabled;
    /// <summary>Comma-separated tokens that allow registering when <see cref="Registration"/> is <see cref="RegistrationMode.Token"/>.</summary>
    [Secret]
    public string? RegistrationTokens { get; set; }

    /// <summary>Addresses of reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are believed, separated by commas, e.g. <c>192.168.1.2</c>; without them, the proxy's own address counts for rate limits and logs.</summary>
    [RestartRequired]
    public string? TrustedProxies { get; set; }

    /// <summary>Return <see cref="TrustedProxies"/> as addresses; throw <see cref="FormatException"/> for one that is not an IP address.</summary>
    public IReadOnlyList<System.Net.IPAddress> GetTrustedProxies() => [.. (TrustedProxies ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(System.Net.IPAddress.Parse)];

    /// <summary>Return the registration tokens as a list.</summary>
    public IReadOnlyList<string> GetRegistrationTokens() => (RegistrationTokens ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
