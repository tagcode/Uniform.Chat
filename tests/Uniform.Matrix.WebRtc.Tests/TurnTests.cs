namespace Uniform.Matrix.WebRtc.Tests;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Uniform.Matrix.WebRtc.Stun;
using Uniform.Matrix.WebRtc.Turn;

/// <summary>Tests of <see cref="TurnClient"/> with <see cref="FakeTurnServer"/>, and with a real TURN server when the environment names one.</summary>
public class TurnTests
{
    private const string Username = "user";
    private const string Password = "pass";

    /// <summary>Allocate after the 401 challenge, and relay both ways with indications and then a channel; delete the allocation when closed.</summary>
    [Fact]
    public async Task RelayThroughFakeServer()
    {
        // Client and peer sockets, and the server
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using FakeTurnServer server = new(Username, Password);
        using UdpClient peer = new(new IPEndPoint(IPAddress.Loopback, 0));
        await using Endpoint endpoint = new(server.Address, Username, Password);
        Assert.True(await endpoint.Turn.AllocateAsync(cancellationToken));
        Assert.Equal(server.RelayAddress, endpoint.Turn.RelayedAddress);
        Assert.Equal(endpoint.LocalAddress, endpoint.Turn.MappedAddress);
        IPEndPoint peerAddress = (IPEndPoint)peer.Client.LocalEndPoint!;

        // Indications both ways once permitted
        Assert.True(await endpoint.Turn.PermitAsync(peerAddress.Address, cancellationToken));
        await AssertRelayedAsync(endpoint, peer, peerAddress, server.RelayAddress, "indication", cancellationToken);

        // A channel both ways
        Assert.True(await endpoint.Turn.BindChannelAsync(peerAddress, cancellationToken));
        await AssertRelayedAsync(endpoint, peer, peerAddress, server.RelayAddress, "channel", cancellationToken);

        // Done
        await endpoint.DisposeAsync();
        Assert.Equal(1, server.Deletes);
    }

    /// <summary>Relay between two allocations on the TURN server that <c>UNIFORM_MATRIX_TURN</c> names, as <c>host:port</c>, with the shared secret in <c>UNIFORM_MATRIX_TURN_SECRET</c>; skipped without them.</summary>
    [Fact]
    public async Task RelayThroughRealServer()
    {
        // Server and time-limited credentials, as a homeserver would hand out
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        if (Environment.GetEnvironmentVariable("UNIFORM_MATRIX_TURN") is not string turn || Environment.GetEnvironmentVariable("UNIFORM_MATRIX_TURN_SECRET") is not string secret) { Assert.Skip("UNIFORM_MATRIX_TURN and UNIFORM_MATRIX_TURN_SECRET are not set."); return; }
        string[] parts = turn.Split(':');
        IPAddress address = (await Dns.GetHostAddressesAsync(parts[0], AddressFamily.InterNetwork, cancellationToken))[0];
        IPEndPoint serverAddress = new(address, int.Parse(parts[1]));
        string username = $"{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}:test";
        string password = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));

        // Two allocations, permitted to each other
        await using Endpoint first = new(serverAddress, username, password);
        await using Endpoint second = new(serverAddress, username, password);
        Assert.True(await first.Turn.AllocateAsync(cancellationToken));
        Assert.True(await second.Turn.AllocateAsync(cancellationToken));
        IPEndPoint firstRelay = first.Turn.RelayedAddress!, secondRelay = second.Turn.RelayedAddress!;
        Assert.True(await first.Turn.PermitAsync(secondRelay.Address, cancellationToken));
        Assert.True(await second.Turn.BindChannelAsync(firstRelay, cancellationToken));

        // Both ways
        await first.Turn.SendAsync("hello"u8.ToArray(), secondRelay, cancellationToken);
        (IPEndPoint from, byte[] data) = await second.Received.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(firstRelay, from);
        Assert.Equal("hello"u8.ToArray(), data);
        await second.Turn.SendAsync("back"u8.ToArray(), firstRelay, cancellationToken);
        (from, data) = await first.Received.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        // Done
        Assert.Equal(secondRelay, from);
        Assert.Equal("back"u8.ToArray(), data);
    }

    /// <summary>Assert that data goes from the client to the peer through the relay, and back.</summary>
    private static async Task AssertRelayedAsync(Endpoint endpoint, UdpClient peer, IPEndPoint peerAddress, IPEndPoint relayAddress, string text, CancellationToken cancellationToken)
    {
        // Out through the relay
        await endpoint.Turn.SendAsync(Encoding.UTF8.GetBytes(text), peerAddress, cancellationToken);
        UdpReceiveResult received = await peer.ReceiveAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(text, Encoding.UTF8.GetString(received.Buffer));
        Assert.Equal(relayAddress, received.RemoteEndPoint);

        // Back from the peer
        await peer.SendAsync(Encoding.UTF8.GetBytes(text + " back"), relayAddress, cancellationToken);
        (IPEndPoint from, byte[] data) = await endpoint.Received.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        // Done
        Assert.Equal(peerAddress, from);
        Assert.Equal(text + " back", Encoding.UTF8.GetString(data));
    }

    /// <summary>A UDP socket with a TURN client on it: reads STUN responses into the transactions, and relayed data into <see cref="Received"/>.</summary>
    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        private readonly CancellationTokenSource stopping = new();
        private readonly Channel<(IPEndPoint From, byte[] Data)> received = Channel.CreateUnbounded<(IPEndPoint, byte[])>();
        private readonly StunClient stun;
        private readonly Task reading;
        private bool disposed;

        /// <summary>Open a socket and a client of <paramref name="server"/>.</summary>
        public Endpoint(IPEndPoint server, string username, string password)
        {
            // Client, then the read loop
            stun = new StunClient(SendAsync) { InitialTimeout = TimeSpan.FromMilliseconds(200) };
            Turn = new TurnClient(stun, SendAsync, server, username, password);
            reading = ReadAsync(stopping.Token);
        }

        /// <summary>The TURN client.</summary>
        public TurnClient Turn { get; }

        /// <summary>The data peers relayed.</summary>
        public ChannelReader<(IPEndPoint From, byte[] Data)> Received => received.Reader;

        /// <summary>The socket's address on loopback.</summary>
        public IPEndPoint LocalAddress => new(IPAddress.Loopback, ((IPEndPoint)socket.Client.LocalEndPoint!).Port);

        /// <summary>Close the client, then the socket.</summary>
        public async ValueTask DisposeAsync()
        {
            // Once
            if (disposed) return;
            disposed = true;
            await Turn.DisposeAsync();
            await stopping.CancelAsync();
            socket.Dispose();
            await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        /// <summary>Send a datagram.</summary>
        private async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint to, CancellationToken cancellationToken) => await socket.SendAsync(datagram, to, cancellationToken);

        /// <summary>Read datagrams until stopped.</summary>
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            // Responses to the transactions, relayed data to the channel
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await socket.ReceiveAsync(cancellationToken);
                if (StunMessage.IsStun(result.Buffer) && stun.Receive(result.Buffer)) continue;
                if (Turn.TryUnwrap(result.Buffer, Normalize(result.RemoteEndPoint), out IPEndPoint peer, out byte[] data)) received.Writer.TryWrite((peer, data));
            }
        }

        /// <summary>Return an address without IPv4 mapping into IPv6, as dual-mode sockets report.</summary>
        private static IPEndPoint Normalize(IPEndPoint address) => address.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(address.Address.MapToIPv4(), address.Port) : address;
    }
}
