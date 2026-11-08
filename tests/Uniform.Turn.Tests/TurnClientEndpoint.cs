namespace Uniform.Turn.Tests;

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using Uniform.Stun;
using Uniform.Stun.Protocol;

/// <summary>A <see cref="TurnClient"/> on its own transport to the server: a UDP socket, or a TCP or TLS connection whose frames it splits and pads as streams need; relayed data goes to <see cref="Received"/>.</summary>
internal sealed class TurnClientEndpoint : IAsyncDisposable
{
    private readonly UdpClient? udp;
    private readonly TcpClient? tcp;
    private Stream? stream;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<(IPEndPoint From, byte[] Data)> received = Channel.CreateUnbounded<(IPEndPoint, byte[])>();
    private readonly IPEndPoint server;
    private Task reading = Task.CompletedTask;
    private bool disposed;

    /// <summary>Create an endpoint; <see cref="ConnectAsync"/> opens a stream transport.</summary>
    private TurnClientEndpoint(IPEndPoint server, string username, string password, TurnTransport transport)
    {
        // Socket of the transport, then the clients
        this.server = server;
        if (transport == TurnTransport.Udp) udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        else tcp = new TcpClient(AddressFamily.InterNetwork);
        Stun = new StunClient(SendAsync) { InitialTimeout = TimeSpan.FromMilliseconds(200), MaxSends = 4 };
        Turn = new TurnClient(Stun, SendAsync, server, username, password);
    }

    /// <summary>The STUN transactions.</summary>
    public StunClient Stun { get; }

    /// <summary>The TURN client.</summary>
    public TurnClient Turn { get; }

    /// <summary>The data peers relayed.</summary>
    public ChannelReader<(IPEndPoint From, byte[] Data)> Received => received.Reader;

    /// <summary>Open an endpoint to <paramref name="server"/> over <paramref name="transport"/>; TLS accepts any certificate, as tests use a self-signed one.</summary>
    public static async Task<TurnClientEndpoint> ConnectAsync(IPEndPoint server, string username, string password, TurnTransport transport = TurnTransport.Udp, CancellationToken cancellationToken = default)
    {
        // Transport, then the read loop
        TurnClientEndpoint endpoint = new(server, username, password, transport);
        if (endpoint.tcp is TcpClient tcp)
        {
            await tcp.ConnectAsync(server, cancellationToken);
            Stream stream = tcp.GetStream();
            if (transport == TurnTransport.Tls)
            {
                SslStream ssl = new(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancellationToken);
                stream = ssl;
            }
            endpoint.stream = stream;
        }
        endpoint.reading = endpoint.ReadAsync(endpoint.stopping.Token);
        // Return endpoint
        return endpoint;
    }

    /// <summary>Close the client, then the transport.</summary>
    public async ValueTask DisposeAsync()
    {
        // Once
        if (disposed) return;
        disposed = true;
        await Turn.DisposeAsync();
        await stopping.CancelAsync();
        udp?.Dispose();
        if (stream is not null) await stream.DisposeAsync();
        tcp?.Dispose();
        await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Send a datagram, or a frame on the stream with channel data padded to four bytes.</summary>
    private async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint to, CancellationToken cancellationToken)
    {
        // UDP as is
        if (udp is not null)
        {
            await udp.SendAsync(datagram, to, cancellationToken);
            return;
        }

        // Streams: padded channel data, one writer at a time
        byte[] frame = ChannelData.IsChannelData(datagram.Span) ? ChannelData.Encode(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(datagram.Span), datagram.Span[4..], padded: true) : datagram.ToArray();
        await writing.WaitAsync(cancellationToken);
        try
        {
            await stream!.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>Read until stopped: responses to the transactions, relayed data to the channel.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        // Datagrams, or frames of the stream
        byte[] buffer = new byte[70000];
        int filled = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (udp is not null)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(cancellationToken);
                Handle(result.Buffer);
                continue;
            }
            int read = await stream!.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0) return;
            filled += read;
            while (ChannelData.FrameLength(buffer.AsSpan(0, filled)) is int length && filled >= length)
            {
                Handle(buffer.AsSpan(0, length).ToArray());
                Buffer.BlockCopy(buffer, length, buffer, 0, filled - length);
                filled -= length;
            }
        }
    }

    /// <summary>Pass a message to the transactions or the relayed data.</summary>
    private void Handle(byte[] message)
    {
        // Responses first
        if (StunMessage.IsStun(message) && Stun.Receive(message)) return;
        if (Turn.TryUnwrap(message, server, out IPEndPoint peer, out byte[] data)) received.Writer.TryWrite((peer, data));
    }
}
