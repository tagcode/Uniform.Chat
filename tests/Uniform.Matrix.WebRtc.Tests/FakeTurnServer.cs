namespace Uniform.Matrix.WebRtc.Tests;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Uniform.Matrix.WebRtc.Stun;

/// <summary>A TURN server just big enough for tests, on loopback: one client, one allocation, long-term credentials with a 401 challenge, permissions, channels, and relaying through its own socket.</summary>
/// <remarks>Its two loops share the client, permissions and channels under one lock.</remarks>
internal sealed class FakeTurnServer : IAsyncDisposable
{
    /// <summary>Realm of the credentials.</summary>
    public const string Realm = "test";
    /// <summary>Nonce of the challenges.</summary>
    public const string Nonce = "nonce";
    private readonly UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient relay = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly byte[] key;
    private readonly HashSet<IPAddress> permitted = [];
    private readonly Dictionary<ushort, IPEndPoint> channels = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Task serving;
    private readonly Task relaying;
    private IPEndPoint? client;

    /// <summary>Start serving a user with a password.</summary>
    public FakeTurnServer(string username, string password)
    {
        // Key of the user, then both sockets read
        key = StunMessage.LongTermKey(username, Realm, password);
        serving = ServeAsync(stopping.Token);
        relaying = RelayAsync(stopping.Token);
    }

    /// <summary>The server's address.</summary>
    public IPEndPoint Address => (IPEndPoint)server.Client.LocalEndPoint!;

    /// <summary>The relayed address of the allocation.</summary>
    public IPEndPoint RelayAddress => (IPEndPoint)relay.Client.LocalEndPoint!;

    /// <summary>Number of Refresh requests with a zero lifetime, which delete the allocation.</summary>
    public int Deletes { get; private set; }

    /// <summary>Stop serving.</summary>
    public async ValueTask DisposeAsync()
    {
        // Both loops end on the cancellation
        await stopping.CancelAsync();
        server.Dispose();
        relay.Dispose();
        await Task.WhenAll(serving, relaying).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Answer the client's requests, and relay its data.</summary>
    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        // Each datagram from the client
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received = await server.ReceiveAsync(cancellationToken);
            IPEndPoint sender = received.RemoteEndPoint;
            lock (gate) client = sender;
            byte[] datagram = received.Buffer;

            // Channel data to a bound peer
            if (datagram[0] is >= 64 and <= 79)
            {
                ushort channel = BinaryPrimitives.ReadUInt16BigEndian(datagram);
                IPEndPoint? bound;
                lock (gate) channels.TryGetValue(channel, out bound);
                if (bound is not null) await relay.SendAsync(datagram.AsMemory(4, BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2))), bound, cancellationToken);
                continue;
            }

            // Send indication to a permitted peer
            if (StunMessage.Parse(datagram) is not StunMessage message) continue;
            IPEndPoint? peer = message.Get(StunAttributeTypes.XorPeerAddress) is byte[] address ? StunAddress.DecodeXor(address, message.TransactionId) : null;
            if (message.Class == StunClass.Indication)
            {
                bool allowed;
                lock (gate) allowed = peer is not null && permitted.Contains(peer.Address);
                if (message.Method == StunMethod.Send && allowed) await relay.SendAsync(message.Get(StunAttributeTypes.Data)!, peer, cancellationToken);
                continue;
            }

            // Requests: challenged without integrity
            StunMessage response;
            lock (gate) response = StunMessage.CheckIntegrity(datagram, key) ? Answer(message, peer) : Error(message, 401);
            await server.SendAsync(response.Encode(response.Class == StunClass.Success ? key : null), sender, cancellationToken);
        }
    }

    /// <summary>Return the response to an authenticated request.</summary>
    private StunMessage Answer(StunMessage request, IPEndPoint? peer)
    {
        // By method
        StunMessage response = new(request.Method, StunClass.Success, request.TransactionId);
        switch (request.Method)
        {
            case StunMethod.Allocate:
                response.Add(StunAttributeTypes.XorRelayedAddress, StunAddress.EncodeXor(RelayAddress, request.TransactionId));
                response.Add(StunAttributeTypes.XorMappedAddress, StunAddress.EncodeXor(client!, request.TransactionId));
                response.Add(StunAttributeTypes.Lifetime, [0, 0, 0x02, 0x58]);
                break;
            case StunMethod.Refresh:
                if (request.Get(StunAttributeTypes.Lifetime) is [0, 0, 0, 0]) Deletes++;
                break;
            case StunMethod.CreatePermission:
                permitted.Add(peer!.Address);
                break;
            case StunMethod.ChannelBind:
                channels[BinaryPrimitives.ReadUInt16BigEndian(request.Get(StunAttributeTypes.ChannelNumber))] = peer!;
                permitted.Add(peer!.Address);
                break;
        }
        // Return response
        return response;
    }

    /// <summary>Return an error response with the realm and nonce.</summary>
    private static StunMessage Error(StunMessage request, int code)
    {
        // Class and number, then the challenge
        StunMessage response = new(request.Method, StunClass.Error, request.TransactionId);
        response.Add(StunAttributeTypes.ErrorCode, [0, 0, (byte)(code / 100), (byte)(code % 100)]);
        // Return error
        return response.Add(StunAttributeTypes.Realm, Realm).Add(StunAttributeTypes.Nonce, Nonce);
    }

    /// <summary>Forward what peers send to the relayed address to the client, on a channel when bound.</summary>
    private async Task RelayAsync(CancellationToken cancellationToken)
    {
        // Each datagram from a peer
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received = await relay.ReceiveAsync(cancellationToken);
            IPEndPoint? to;
            KeyValuePair<ushort, IPEndPoint> bound;
            lock (gate)
            {
                to = client;
                if (to is null || !permitted.Contains(received.RemoteEndPoint.Address)) continue;
                bound = channels.FirstOrDefault(c => c.Value.Equals(received.RemoteEndPoint));
            }
            byte[] data = received.Buffer;

            // Channel data
            if (bound.Value is not null)
            {
                byte[] datagram = new byte[4 + data.Length];
                BinaryPrimitives.WriteUInt16BigEndian(datagram, bound.Key);
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)data.Length);
                data.CopyTo(datagram, 4);
                await server.SendAsync(datagram, to, cancellationToken);
                continue;
            }

            // Data indication
            StunMessage indication = new(StunMethod.Data, StunClass.Indication);
            indication.Add(StunAttributeTypes.XorPeerAddress, StunAddress.EncodeXor(received.RemoteEndPoint, indication.TransactionId)).Add(StunAttributeTypes.Data, data);
            await server.SendAsync(indication.Encode(), to, cancellationToken);
        }
    }
}
