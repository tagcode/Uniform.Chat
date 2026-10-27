namespace Uniform.Matrix.Server.Http;

using System.Net;
using System.Net.Sockets;

/// <summary>HTTP clients that reach addresses users give, such as push gateways and pages to preview, without letting users make the server reach machines inside its network.</summary>
public static class PublicAddresses
{
    /// <summary>Return a handler that connects only to public addresses unless <paramref name="allowPrivate"/> says otherwise, checking the very addresses it connects to, so a host cannot resolve to a public address for the check and a private one for the request; redirects are checked the same way.</summary>
    /// <param name="allowPrivate">Whether private addresses are allowed now, read on every connection so a setting applies at once.</param>
    /// <param name="setting">Name of the setting, for the error message.</param>
    public static SocketsHttpHandler CreateHandler(Func<bool> allowPrivate, string setting) => new()
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            // Resolve once; every address must be public when private ones are not allowed
            string host = context.DnsEndPoint.Host;
            IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (!allowPrivate() && !addresses.All(IsPublic)) throw new HttpRequestException($"{host} is a private address; {setting} allows it.");

            // Connect to the checked addresses
            Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                // Return stream
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Return whether an address is public: not loopback, private, link-local, shared, unique-local or multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        // IPv4 mapped into IPv6 counts as IPv4
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);
        byte[] bytes = address.GetAddressBytes();
        // Return whether it is outside the private, link-local, shared and multicast ranges
        return !(bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) || bytes[0] == 0 || bytes[0] >= 224);
    }
}
