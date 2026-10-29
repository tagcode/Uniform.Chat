namespace Uniform.Matrix.WebRtc.Turn;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Uniform.Matrix.WebRtc.Stun;

/// <summary>A <see href="https://www.rfc-editor.org/rfc/rfc8656">TURN</see> client over UDP: allocates a relayed address on a TURN server, permits peers, and relays datagrams to and from them, refreshing all until closed.</summary>
/// <remarks>
/// The socket is shared, e.g. with ICE: the owner reads it, gives STUN responses to <see cref="StunClient.Receive"/>, and asks <see cref="TryUnwrap"/> for the data of datagrams from the server.
/// Channels carry data with four bytes of overhead instead of a STUN indication's thirty-six; <see cref="SendAsync"/> uses one once <see cref="BindChannelAsync"/> has bound it.
/// </remarks>
public sealed class TurnClient : IAsyncDisposable
{
    /// <summary>The protocol number of UDP, for REQUESTED-TRANSPORT.</summary>
    private const byte Udp = 17;
    /// <summary>The first channel number.</summary>
    private const ushort FirstChannel = 0x4000;
    /// <summary>How long a permission lasts on the server.</summary>
    private static readonly TimeSpan PermissionLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long a channel binding lasts on the server.</summary>
    private static readonly TimeSpan ChannelLifetime = TimeSpan.FromMinutes(10);
    /// <summary>How often refreshes are due-checked.</summary>
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    private readonly StunClient stun;
    private readonly DatagramSender send;
    private readonly string username;
    private readonly string password;
    private readonly ConcurrentDictionary<IPAddress, DateTime> permissions = new();
    private readonly ConcurrentDictionary<IPEndPoint, (ushort Channel, DateTime Bound)> channelsByPeer = new();
    private readonly ConcurrentDictionary<ushort, IPEndPoint> peersByChannel = new();
    private readonly CancellationTokenSource closing = new();
    private string? realm;
    private string? nonce;
    private byte[]? key;
    private DateTime expires;
    private int nextChannel = FirstChannel;
    private Task? maintenance;

    /// <summary>Create a client of the TURN server at <paramref name="server"/>.</summary>
    /// <param name="stun">STUN transactions over the shared socket.</param>
    /// <param name="send">Sends a datagram over the shared socket, for channel data.</param>
    /// <param name="server">The TURN server's UDP address.</param>
    /// <param name="username">Username, e.g. of the time-limited credentials of a Matrix homeserver's <c>/voip/turnServer</c>.</param>
    /// <param name="password">Password of <paramref name="username"/>.</param>
    public TurnClient(StunClient stun, DatagramSender send, IPEndPoint server, string username, string password)
    {
        // Stored for the requests
        this.stun = stun;
        this.send = send;
        Server = server;
        this.username = username;
        this.password = password;
    }

    /// <summary>The TURN server's address.</summary>
    public IPEndPoint Server { get; }

    /// <summary>The relayed address peers send to, once allocated.</summary>
    public IPEndPoint? RelayedAddress { get; private set; }

    /// <summary>This client's public address as the server saw it, once allocated; a server-reflexive candidate.</summary>
    public IPEndPoint? MappedAddress { get; private set; }

    /// <summary>Allocate a relayed address; return whether the server gave one.</summary>
    public async Task<bool> AllocateAsync(CancellationToken cancellationToken = default)
    {
        // Allocate for UDP; the first attempt learns the realm and nonce
        byte[] transport = [Udp, 0, 0, 0];
        StunMessage? response = await RequestAsync(() => new StunMessage(StunMethod.Allocate, StunClass.Request).Add(StunAttributeTypes.RequestedTransport, transport), cancellationToken);
        if (response?.Class != StunClass.Success) return false;
        if (response.Get(StunAttributeTypes.XorRelayedAddress) is not byte[] relayed) return false;

        // Addresses and lifetime, then refreshes until closed
        RelayedAddress = StunAddress.DecodeXor(relayed, response.TransactionId);
        MappedAddress = response.Get(StunAttributeTypes.XorMappedAddress) is byte[] mapped ? StunAddress.DecodeXor(mapped, response.TransactionId) : null;
        expires = DateTime.UtcNow + Lifetime(response);
        maintenance ??= MaintainAsync(closing.Token);
        // Done
        return RelayedAddress is not null;
    }

    /// <summary>Permit a peer's address to send to the relayed address, and this client to send to it; return whether the server agreed.</summary>
    public async Task<bool> PermitAsync(IPAddress peer, CancellationToken cancellationToken = default)
    {
        // One permission per IP address, any port
        StunMessage? response = await RequestAsync(() => new StunMessage(StunMethod.CreatePermission, StunClass.Request).Add(StunAttributeTypes.XorPeerAddress, []), cancellationToken, peer: new IPEndPoint(peer, 0));
        if (response?.Class != StunClass.Success) return false;
        permissions[peer] = DateTime.UtcNow;
        // Done
        return true;
    }

    /// <summary>Bind a channel to a peer, which also permits it; return whether the server agreed.</summary>
    public async Task<bool> BindChannelAsync(IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        // The peer's channel, or a new one
        ushort channel = channelsByPeer.TryGetValue(peer, out (ushort Channel, DateTime Bound) bound) ? bound.Channel : (ushort)Interlocked.Increment(ref nextChannel);
        if (channel > 0x4FFF) return false;
        byte[] number = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(number, channel);
        StunMessage? response = await RequestAsync(() => new StunMessage(StunMethod.ChannelBind, StunClass.Request).Add(StunAttributeTypes.ChannelNumber, number).Add(StunAttributeTypes.XorPeerAddress, []), cancellationToken, peer);
        if (response?.Class != StunClass.Success) return false;

        // Both ways, and the permission it brings
        DateTime now = DateTime.UtcNow;
        channelsByPeer[peer] = (channel, now);
        peersByChannel[channel] = peer;
        permissions[peer.Address] = now;
        // Done
        return true;
    }

    /// <summary>Relay <paramref name="data"/> to <paramref name="peer"/>, on its channel when bound, else in a Send indication; the peer must be permitted.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        // Channel data: number, length, data
        if (channelsByPeer.TryGetValue(peer, out (ushort Channel, DateTime Bound) bound))
        {
            byte[] datagram = new byte[4 + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(datagram, bound.Channel);
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)data.Length);
            data.Span.CopyTo(datagram.AsSpan(4));
            return send(datagram, Server, cancellationToken);
        }

        // Send indication
        StunMessage indication = new(StunMethod.Send, StunClass.Indication);
        indication.Add(StunAttributeTypes.XorPeerAddress, StunAddress.EncodeXor(peer, indication.TransactionId)).Add(StunAttributeTypes.Data, data.ToArray());
        // Done
        return stun.IndicateAsync(indication, Server, cancellationToken);
    }

    /// <summary>Return whether <paramref name="datagram"/>, received from <paramref name="from"/>, is data the server relays from a peer: a Data indication or channel data.</summary>
    /// <param name="datagram">Datagram as received.</param>
    /// <param name="from">Address it came from; only the server's are unwrapped.</param>
    /// <param name="peer">The peer that sent the data.</param>
    /// <param name="data">The data the peer sent.</param>
    public bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint from, out IPEndPoint peer, out byte[] data)
    {
        // From the server only
        peer = null!;
        data = [];
        if (!from.Equals(Server) || datagram.Length < 4) return false;

        // Channel data: the first byte is 64 to 79, as RFC 7983 tells it apart
        if (datagram[0] is >= 64 and <= 79)
        {
            ushort channel = BinaryPrimitives.ReadUInt16BigEndian(datagram);
            int length = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
            if (4 + length > datagram.Length || !peersByChannel.TryGetValue(channel, out IPEndPoint? bound)) return false;
            peer = bound;
            data = datagram.Slice(4, length).ToArray();
            return true;
        }

        // Data indication
        if (StunMessage.Parse(datagram) is not { Method: StunMethod.Data, Class: StunClass.Indication } indication) return false;
        if (indication.Get(StunAttributeTypes.XorPeerAddress) is not byte[] address || StunAddress.DecodeXor(address, indication.TransactionId) is not IPEndPoint sender) return false;
        if (indication.Get(StunAttributeTypes.Data) is not byte[] payload) return false;
        peer = sender;
        data = payload;
        // Done
        return true;
    }

    /// <summary>Release the allocation, and stop refreshing.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop the refreshes
        await closing.CancelAsync();
        if (maintenance is not null) await maintenance.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        closing.Dispose();
        if (RelayedAddress is null) return;

        // A lifetime of zero deletes the allocation; a short wait, as the call is ending anyway
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        byte[] zero = new byte[4];
        try { await RequestAsync(() => new StunMessage(StunMethod.Refresh, StunClass.Request).Add(StunAttributeTypes.Lifetime, zero), timeout.Token); }
        catch (OperationCanceledException) { }
        // Done
        RelayedAddress = null;
    }

    /// <summary>Refresh the allocation, permissions and channels before they expire, until <paramref name="cancellationToken"/> cancels.</summary>
    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        // Every interval, what expires within two intervals and a minute
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(MaintenanceInterval, cancellationToken);
                DateTime soon = DateTime.UtcNow + 2 * MaintenanceInterval + TimeSpan.FromMinutes(1);
                if (expires < soon) await RefreshAllocationAsync(cancellationToken);
                foreach ((IPEndPoint peer, (ushort _, DateTime bound)) in channelsByPeer)
                {
                    if (bound + ChannelLifetime < soon) await BindChannelAsync(peer, cancellationToken);
                }
                foreach ((IPAddress peer, DateTime permitted) in permissions)
                {
                    if (permitted + PermissionLifetime < soon) await PermitAsync(peer, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed
        }
    }

    /// <summary>Refresh the allocation with the lifetime the server chooses.</summary>
    private async Task RefreshAllocationAsync(CancellationToken cancellationToken)
    {
        // Refresh, then the new expiry
        StunMessage? response = await RequestAsync(() => new StunMessage(StunMethod.Refresh, StunClass.Request), cancellationToken);
        if (response?.Class != StunClass.Success) return;
        // Done
        expires = DateTime.UtcNow + Lifetime(response);
    }

    /// <summary>Send an authenticated request built by <paramref name="build"/>; on a 401 or a stale nonce (438), take the server's realm and nonce, and send it once more.</summary>
    /// <param name="build">Makes the request; called again for the retry, as it needs a new transaction ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="peer">The peer whose address replaces the empty XOR-PEER-ADDRESS, which depends on the transaction ID.</param>
    private async Task<StunMessage?> RequestAsync(Func<StunMessage> build, CancellationToken cancellationToken, IPEndPoint? peer = null)
    {
        // Once with what is known, and again after a challenge
        for (int attempt = 0; attempt < 2; attempt++)
        {
            StunMessage request = Authenticate(build(), peer);
            StunMessage? response = await stun.RequestAsync(request, Server, key, cancellationToken: cancellationToken);
            if (response?.ErrorCode is not (401 or 438)) return response;
            if (response.GetText(StunAttributeTypes.Nonce) is not string challenge) return response;
            realm = response.GetText(StunAttributeTypes.Realm) ?? realm;
            nonce = challenge;
            key = realm is null ? null : StunMessage.LongTermKey(username, realm, password);
        }
        // On a second challenge
        return null;
    }

    /// <summary>Return <paramref name="request"/> with the peer address filled in, and the credentials once the server has given its realm and nonce.</summary>
    private StunMessage Authenticate(StunMessage request, IPEndPoint? peer)
    {
        // The peer's address, XOR'ed with this transaction's ID
        if (peer is not null)
        {
            int index = request.Attributes.FindIndex(a => a.Type == StunAttributeTypes.XorPeerAddress);
            request.Attributes[index] = new StunAttribute(StunAttributeTypes.XorPeerAddress, StunAddress.EncodeXor(peer, request.TransactionId));
        }

        // Credentials
        if (realm is null || nonce is null) return request;
        request.Add(StunAttributeTypes.Username, username).Add(StunAttributeTypes.Realm, realm).Add(StunAttributeTypes.Nonce, nonce);
        // Return request
        return request;
    }

    /// <summary>Return the LIFETIME of a response, or the default of ten minutes.</summary>
    private static TimeSpan Lifetime(StunMessage response) =>
        response.Get(StunAttributeTypes.Lifetime) is { Length: 4 } value ? TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt32BigEndian(value)) : TimeSpan.FromMinutes(10);
}
