namespace Uniform.Matrix.Protocol.Server;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

/// <summary><see href="https://spec.matrix.org/latest/appendices/#server-name">Server names</see>: a DNS name, IPv4 address or bracketed IPv6 address, with an optional port.</summary>
public static class ServerNames
{
    /// <summary>Default federation port when the name and its delegation give none.</summary>
    public const int DefaultPort = 8448;

    /// <summary>Split <paramref name="serverName"/> into host and port; false when it is not a valid server name.</summary>
    /// <param name="serverName">E.g. <c>example.org</c>, <c>example.org:8448</c>, <c>1.2.3.4</c> or <c>[::1]:8448</c>.</param>
    /// <param name="host">Host as written, IPv6 with its brackets.</param>
    /// <param name="port">Explicit port, or null.</param>
    public static bool TryParse([NotNullWhen(true)] string? serverName, [NotNullWhen(true)] out string? host, out int? port)
    {
        // Host, then an optional port after the host's end
        host = null;
        port = null;
        if (string.IsNullOrEmpty(serverName) || serverName.Length > 255) return false;
        int hostEnd = serverName[0] == '[' ? serverName.IndexOf(']') + 1 : serverName.IndexOf(':') is int colon and >= 0 ? colon : serverName.Length;
        if (hostEnd <= 0) return false;
        string candidate = serverName[..hostEnd];
        if (hostEnd < serverName.Length)
        {
            if (serverName[hostEnd] != ':' || !int.TryParse(serverName.AsSpan(hostEnd + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535) return false;
            port = number;
        }

        // IPv6 in brackets, IPv4, or DNS name characters
        if (candidate[0] == '[')
        {
            if (!IPAddress.TryParse(candidate[1..^1], out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else if (!candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            return false;
        }
        host = candidate;
        // Done
        return true;
    }

    /// <summary>Return whether <paramref name="host"/> from <see cref="TryParse"/> is an IP address literal rather than a DNS name.</summary>
    public static bool IsIpLiteral(string host) => host.StartsWith('[') || IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host.Count(c => c == '.') == 3;
}
