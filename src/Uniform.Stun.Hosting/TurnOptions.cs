namespace Uniform.Stun.Hosting;

using Uniform.Matrix.Hosting;

/// <summary>Settings of the STUN and TURN server in section <c>Turn</c>, e.g. <c>--Turn:Enabled=true --Turn:ExternalAddress=203.0.113.5</c>; every change takes effect after a restart, as the server binds its ports at start.</summary>
public sealed class TurnOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Turn";

    /// <summary>Whether to run the STUN and TURN server.</summary>
    [RestartRequired]
    public bool Enabled { get; set; }

    /// <summary>Realm of the credentials, usually the server's domain; <c>turn</c> when not set, or the homeserver's name when embedded.</summary>
    [RestartRequired]
    public string? Realm { get; set; }

    /// <summary>Address to listen on; 0.0.0.0 for every IPv4 interface.</summary>
    [RestartRequired]
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Port of STUN and TURN over UDP and TCP; 3478 is the standard.</summary>
    [RestartRequired]
    public int Port { get; set; } = 3478;

    /// <summary>Whether to listen over TCP on the same port too, for networks that block UDP.</summary>
    [RestartRequired]
    public bool Tcp { get; set; } = true;

    /// <summary>Port of TURN over TLS, e.g. 5349, or 443 on networks that only let web traffic through; none when not set. Needs a certificate.</summary>
    [RestartRequired]
    public int? TlsPort { get; set; }

    /// <summary>PEM file of the TLS certificate chain, e.g. fullchain.pem of Let's Encrypt; renewed files are picked up without a restart.</summary>
    [RestartRequired]
    public string? CertificatePath { get; set; }

    /// <summary>PEM file of the TLS certificate's private key, e.g. privkey.pem.</summary>
    [RestartRequired]
    public string? KeyPath { get; set; }

    /// <summary>Public IPv4 address that peers send relayed data to, the router's public address when the server is behind NAT: a fixed address; a host name, e.g. of a dynamic DNS service, resolved again every check; or stun:host:port, asking that STUN server every check. A changed address, e.g. after the router's DHCP lease, goes to new allocations.</summary>
    [RestartRequired]
    public string? ExternalAddress { get; set; }

    /// <summary>How often the server checks its local and public addresses for changes, besides when the system reports one.</summary>
    [RestartRequired]
    public TimeSpan AddressCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Address the relay sockets bind; the listen address when not set.</summary>
    [RestartRequired]
    public string? RelayAddress { get; set; }

    /// <summary>First UDP port of relayed addresses; behind NAT, forward the whole range to the server.</summary>
    [RestartRequired]
    public int MinRelayPort { get; set; } = 49152;

    /// <summary>Last UDP port of relayed addresses.</summary>
    [RestartRequired]
    public int MaxRelayPort { get; set; } = 65535;

    /// <summary>Most allocations at once; each call uses one or two.</summary>
    [RestartRequired]
    public int MaxAllocations { get; set; } = 1000;

    /// <summary>Most allocations at once per user.</summary>
    [RestartRequired]
    public int MaxAllocationsPerUser { get; set; } = 10;

    /// <summary>Addresses relayed to although they are not public, separated by commas, e.g. 192.168.1.0/24 to let calls reach a local network on purpose; private, loopback and link-local addresses are refused otherwise.</summary>
    [RestartRequired]
    public string? AllowedPeers { get; set; }

    /// <summary>Shared secrets of time-limited credentials, separated by commas, as a homeserver's Calls:TurnSharedSecret hands them out; not needed when the server runs inside the homeserver.</summary>
    [RestartRequired]
    [Secret]
    public string? SharedSecrets { get; set; }

    /// <summary>Fixed users as name:password, separated by commas, for clients without a homeserver.</summary>
    [RestartRequired]
    [Secret]
    public string? Users { get; set; }
}
