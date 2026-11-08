namespace Uniform.Turn;

using System.Net;
using System.Net.Sockets;

/// <summary>A range of IP addresses in CIDR notation, such as <c>10.0.0.0/8</c> or <c>fe80::/10</c>; a single address is a range of one.</summary>
/// <param name="Network">First address of the range.</param>
/// <param name="PrefixLength">Number of leading bits that all addresses of the range share.</param>
public sealed record IpRange(IPAddress Network, int PrefixLength)
{
    /// <summary>Ranges a TURN server should not relay to, so outsiders cannot reach its local network or itself: unspecified, loopback, private, carrier-grade NAT, link-local, multicast and reserved addresses of IPv4 and IPv6.</summary>
    public static IReadOnlyList<IpRange> NonPublic { get; } = [.. new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "::/128", "::1/128", "64:ff9b::/96", "100::/64", "2001:db8::/32", "fc00::/7", "fe80::/10", "ff00::/8",
    }.Select(Parse)];

    /// <summary>Return the range of <paramref name="text"/>, e.g. <c>192.168.0.0/16</c> or <c>192.168.1.5</c>; throw <see cref="FormatException"/> when it is not one.</summary>
    public static IpRange Parse(string text)
    {
        // Address and optional prefix
        string[] parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out IPAddress? address)) throw new FormatException($"\"{text}\" is not an IP address or a range like 10.0.0.0/8.");
        int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = parts.Length == 1 ? bits : int.TryParse(parts[1], out int given) && given >= 0 && given <= bits ? given : throw new FormatException($"\"{text}\" has a prefix length outside 0 to {bits}.");
        // Return range
        return new IpRange(address, prefix);
    }

    /// <summary>Return the ranges of a comma-separated list, e.g. from settings; an empty list for null or empty text.</summary>
    public static IReadOnlyList<IpRange> ParseList(string? text) => [.. (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Parse)];

    /// <summary>Return whether <paramref name="address"/> is in the range; IPv4 addresses mapped into IPv6 count as IPv4.</summary>
    public bool Contains(IPAddress address)
    {
        // Same family, then the prefix bits
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != Network.AddressFamily) return false;
        byte[] a = address.GetAddressBytes(), n = Network.GetAddressBytes();
        int full = PrefixLength / 8, rest = PrefixLength % 8;
        for (int i = 0; i < full; i++) if (a[i] != n[i]) return false;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        // Return whether the partial byte matches
        return (a[full] & mask) == (n[full] & mask);
    }

    /// <summary>Return the range in CIDR notation.</summary>
    public override string ToString() => $"{Network}/{PrefixLength}";
}
