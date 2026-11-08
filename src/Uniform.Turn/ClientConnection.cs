namespace Uniform.Turn;

using System.Net;
using System.Net.Sockets;

/// <summary>How a client reaches the TURN server; the relay towards peers is always UDP.</summary>
public enum TurnTransport
{
    /// <summary>UDP datagrams.</summary>
    Udp,
    /// <summary>A TCP connection.</summary>
    Tcp,
    /// <summary>A TLS connection over TCP.</summary>
    Tls,
}

/// <summary>A client of the server as one transport sees it: its address, and how messages get back to it.</summary>
/// <remarks>The transport and the two addresses form the 5-tuple that names the client's allocation.</remarks>
internal abstract class ClientConnection
{
    /// <summary>Create a connection between <paramref name="remote"/> and <paramref name="local"/>.</summary>
    protected ClientConnection(TurnTransport transport, IPEndPoint remote, IPEndPoint local)
    {
        // Addresses without IPv4 mapped into IPv6, as dual-mode sockets report
        Transport = transport;
        Remote = Normalize(remote);
        Local = Normalize(local);
        Key = $"{transport}/{Remote}/{Local}";
    }

    /// <summary>The transport.</summary>
    public TurnTransport Transport { get; }

    /// <summary>The client's address as the server sees it.</summary>
    public IPEndPoint Remote { get; }

    /// <summary>The server's address the client reached.</summary>
    public IPEndPoint Local { get; }

    /// <summary>Key of the 5-tuple.</summary>
    public string Key { get; }

    /// <summary>Whether messages travel in a stream, where channel data is padded to four bytes.</summary>
    public bool IsStream => Transport != TurnTransport.Udp;

    /// <summary>Send a STUN message or channel data to the client; a closed connection drops it.</summary>
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Return an address without IPv4 mapped into IPv6.</summary>
    public static IPEndPoint Normalize(IPEndPoint address) => address.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(address.Address.MapToIPv4(), address.Port) : address;
}

/// <summary>A client over the server's UDP socket.</summary>
/// <param name="socket">The listening socket, which answers too.</param>
/// <param name="remote">The client's address.</param>
/// <param name="local">The socket's address.</param>
internal sealed class UdpConnection(Socket socket, IPEndPoint remote, IPEndPoint local) : ClientConnection(TurnTransport.Udp, remote, local)
{
    /// <summary>The address to send to, in the socket's family.</summary>
    private readonly IPEndPoint target = socket.AddressFamily == AddressFamily.InterNetworkV6 && remote.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(remote.Address.MapToIPv6(), remote.Port) : remote;

    /// <summary>Send a datagram; errors drop it, as UDP does.</summary>
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // A lost datagram is like a dropped one
        try
        {
            await socket.SendToAsync(message, SocketFlags.None, target, cancellationToken);
        }
        catch (SocketException)
        {
            // Dropped
        }
        catch (ObjectDisposedException)
        {
            // Stopped
        }
    }
}

/// <summary>A client over a TCP or TLS connection; writes are serialized, as relayed data and responses interleave.</summary>
/// <param name="transport">TCP or TLS.</param>
/// <param name="stream">The connection's stream.</param>
/// <param name="remote">The client's address.</param>
/// <param name="local">The server's address.</param>
internal sealed class StreamConnection(TurnTransport transport, Stream stream, IPEndPoint remote, IPEndPoint local) : ClientConnection(transport, remote, local)
{
    private readonly SemaphoreSlim writing = new(1, 1);

    /// <summary>Whether the connection has closed.</summary>
    public bool Closed { get; private set; }

    /// <summary>Write the message; a closed connection drops it.</summary>
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // One writer at a time
        if (Closed) return;
        await writing.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(message, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Closed = true;
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>Mark the connection closed, so later writes are dropped.</summary>
    public void Close() => Closed = true;
}
