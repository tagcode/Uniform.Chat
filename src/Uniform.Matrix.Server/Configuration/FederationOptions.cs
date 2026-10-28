namespace Uniform.Matrix.Server.Configuration;

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/">Federation</see> with other homeservers, configuration section <see cref="Section"/>.</summary>
public sealed record FederationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Federation";

    /// <summary>Whether to talk to other servers and answer them; off while federation is unfinished.</summary>
    public bool Enabled { get; set; }
    /// <summary>Where other servers should connect, announced in <c>/.well-known/matrix/server</c>, e.g. <c>example.org:443</c> when a reverse proxy on port 443 forwards to this server; not served when unset, so other servers use port 8448 or the port in the server name.</summary>
    public string? WellKnownServer { get; set; }
    /// <summary>Whether to reach servers at private, loopback and link-local addresses; off, so remote names cannot make the server reach machines inside its network.</summary>
    public bool AllowPrivateAddresses { get; set; }
    /// <summary>How long to wait for another server to answer.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for a server's signing keys, from it or a key server; short, as a join waits for the keys of every server in the room, and some are always down.</summary>
    public TimeSpan KeyRequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long to wait for another server to answer a join, which sends the whole state of the room; large rooms take minutes.</summary>
    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromMinutes(3);
    /// <summary>Key servers (notaries) to ask for other servers' signing keys when those servers cannot be reached, separated by commas, e.g. <c>matrix.org</c>; none by default, as asking tells them which servers this one talks to.</summary>
    public string? TrustedKeyServers { get; set; }
    /// <summary>How long other servers may cache this server's signing key, announced as <c>valid_until_ts</c>.</summary>
    public TimeSpan KeyValidity { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Return <see cref="TrustedKeyServers"/> as a list.</summary>
    public IReadOnlyList<string> GetTrustedKeyServers() => (TrustedKeyServers ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
