namespace Uniform.Turn;

using System.Net;

/// <summary>Settings of a <see cref="TurnServer"/>: where it listens, which relay ports and address it uses, how long allocations last, and the limits that keep it from becoming an open relay.</summary>
public sealed class TurnServerOptions
{
    /// <summary>Realm of the long-term credentials, usually the server's domain.</summary>
    public string Realm { get; set; } = "turn";

    /// <summary>Address the listeners bind, e.g. <c>0.0.0.0</c> for every IPv4 interface; then UDP binds each address of the interfaces that are up at start, and relays the address the client reached, so traffic leaves the way it came on computers with several networks.</summary>
    public IPAddress ListenAddress { get; set; } = IPAddress.Any;

    /// <summary>Port of STUN and TURN over UDP and TCP; 3478 is the standard. 0 picks a free port, e.g. in tests.</summary>
    public int Port { get; set; } = 3478;

    /// <summary>Whether to listen over TCP on <see cref="Port"/> too, for networks that block UDP.</summary>
    public bool Tcp { get; set; } = true;

    /// <summary>Port of TURN over TLS, e.g. 5349 or 443; null for none. Needs <see cref="Certificate"/>.</summary>
    public int? TlsPort { get; set; }

    /// <summary>Returns the TLS certificate with its key for each new TLS connection, so renewed certificates are picked up; null without TLS.</summary>
    public Func<System.Security.Cryptography.X509Certificates.X509Certificate2>? Certificate { get; set; }

    /// <summary>Address relay sockets bind; when null, <see cref="ListenAddress"/>, or the address each client reached when that is <c>0.0.0.0</c>.</summary>
    public IPAddress? RelayAddress { get; set; }

    /// <summary>Public address peers send to, which clients learn as their relayed address: the router's public address when the server is behind NAT. When null, the relay sockets' address, which must then not be <c>0.0.0.0</c>.</summary>
    public IPAddress? ExternalAddress { get; set; }

    /// <summary>First port of relayed addresses; behind NAT, forward the range from <see cref="MinRelayPort"/> to <see cref="MaxRelayPort"/> over UDP.</summary>
    public int MinRelayPort { get; set; } = 49152;

    /// <summary>Last port of relayed addresses.</summary>
    public int MaxRelayPort { get; set; } = 65535;

    /// <summary>Lifetime of an allocation when the client asks for none or less; it ends unless refreshed.</summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Longest lifetime of an allocation per refresh.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a nonce is accepted; after it, requests get 438 Stale Nonce with a new one.</summary>
    public TimeSpan NonceLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Most allocations at once on the server.</summary>
    public int MaxAllocations { get; set; } = 1000;

    /// <summary>Most allocations at once per user name; a call needs one or two.</summary>
    public int MaxAllocationsPerUser { get; set; } = 10;

    /// <summary>Peer addresses not relayed to; by default <see cref="IpRange.NonPublic"/>.</summary>
    public IReadOnlyList<IpRange> DeniedPeers { get; set; } = IpRange.NonPublic;

    /// <summary>Peer addresses relayed to even when <see cref="DeniedPeers"/> has them, e.g. the server's own local network on purpose.</summary>
    public IReadOnlyList<IpRange> AllowedPeers { get; set; } = [];

    /// <summary>Software name in responses.</summary>
    public string Software { get; set; } = "Uniform.Turn";
}
