namespace Uniform.Turn;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Uniform.Stun.Protocol;

/// <summary>What a <see cref="TurnServer"/> is doing.</summary>
/// <param name="Allocations">Allocations now.</param>
/// <param name="AllocationsMade">Allocations made since start.</param>
/// <param name="BytesRelayed">Bytes relayed both ways since start.</param>
/// <param name="Listening">Addresses the listeners have, e.g. <c>udp 0.0.0.0:3478</c>.</param>
public sealed record TurnServerStatus(int Allocations, long AllocationsMade, long BytesRelayed, IReadOnlyList<string> Listening);

/// <summary>A <see href="https://www.rfc-editor.org/rfc/rfc8489">STUN</see> and <see href="https://www.rfc-editor.org/rfc/rfc8656">TURN</see> server: answers Binding requests, and relays UDP between authenticated clients and their peers. Clients reach it over UDP, TCP or TLS; peers over UDP.</summary>
/// <remarks>
/// It refuses to relay to <see cref="TurnServerOptions.DeniedPeers"/>, by default every address that is not public, so it cannot be used to reach the server's own network.
/// Data between two allocations of this server goes from one to the other directly, so clients behind the same router as the server need no NAT hairpinning.
/// <list type="bullet">
/// <item><description>TurnServer.cs: listeners, relaying, expiry and status.</description></item>
/// <item><description>TurnServer.Requests.cs: STUN and TURN requests, indications and channel data.</description></item>
/// </list>
/// </remarks>
public sealed partial class TurnServer : IAsyncDisposable
{
    /// <summary>Largest datagram relayed; larger ones are cut by the socket.</summary>
    private const int MaxDatagram = 65535;
    /// <summary>How often expired allocations, permissions and channels are removed.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private readonly TurnServerOptions options;
    private readonly ITurnAuthenticator authenticator;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly Nonces nonces;
    private readonly ConcurrentDictionary<string, Allocation> allocations = new();
    private readonly ConcurrentDictionary<int, Allocation> allocationsByPort = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> loops = [];
    private readonly List<string> listening = [];
    private readonly List<IDisposable> listeners = [];
    private long allocationsMade;
    private long bytesRelayed;
    private IPAddress relayAddress = IPAddress.Any;
    private IPAddress externalAddress = IPAddress.Any;
    private bool started;

    /// <summary>Create a server; <see cref="StartAsync"/> starts it.</summary>
    /// <param name="options">Where it listens and its limits.</param>
    /// <param name="authenticator">Keys of the users' credentials.</param>
    /// <param name="logger">Log of allocations and errors; not of each packet.</param>
    /// <param name="time">Clock of lifetimes and nonces.</param>
    public TurnServer(TurnServerOptions options, ITurnAuthenticator authenticator, ILogger<TurnServer> logger, TimeProvider? time = null)
    {
        // Store dependencies
        this.options = options;
        this.authenticator = authenticator;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        nonces = new Nonces(this.time, options.NonceLifetime);
    }

    /// <summary>The UDP and TCP address, once started; its port is the one chosen when <see cref="TurnServerOptions.Port"/> is 0.</summary>
    public IPEndPoint? UdpEndPoint { get; private set; }

    /// <summary>The TCP address, once started, when listening over TCP.</summary>
    public IPEndPoint? TcpEndPoint { get; private set; }

    /// <summary>The TLS address, once started, when listening over TLS.</summary>
    public IPEndPoint? TlsEndPoint { get; private set; }

    /// <summary>Return what the server is doing.</summary>
    public TurnServerStatus Status => new(allocations.Count, Interlocked.Read(ref allocationsMade), Interlocked.Read(ref bytesRelayed), [.. listening]);

    /// <summary>Bind the listeners and start serving; throw <see cref="SocketException"/> when a port is taken, and <see cref="InvalidOperationException"/> for settings that cannot work.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Assert settings, then the addresses relayed addresses use
        if (started) throw new InvalidOperationException("The TURN server is already started.");
        relayAddress = options.RelayAddress ?? options.ListenAddress;
        externalAddress = options.ExternalAddress ?? relayAddress;
        if (externalAddress.Equals(IPAddress.Any) || externalAddress.Equals(IPAddress.IPv6Any)) throw new InvalidOperationException("Set the TURN server's external address: the public address peers send to, as it listens on every interface.");
        if (externalAddress.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("The TURN server relays over IPv4; its external address must be an IPv4 address.");
        if (options.MinRelayPort < 1 || options.MaxRelayPort > 65535 || options.MinRelayPort > options.MaxRelayPort) throw new InvalidOperationException("The relay ports must be a range within 1 to 65535.");
        if (options.TlsPort is not null && options.Certificate is null) throw new InvalidOperationException("TURN over TLS needs a certificate.");
        started = true;

        // UDP, then TCP on the same port, then TLS
        Socket udp = new(options.ListenAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (options.ListenAddress.AddressFamily == AddressFamily.InterNetworkV6) udp.DualMode = true;
        udp.Bind(new IPEndPoint(options.ListenAddress, options.Port));
        listeners.Add(udp);
        UdpEndPoint = (IPEndPoint)udp.LocalEndPoint!;
        listening.Add($"udp {UdpEndPoint}");
        loops.Add(ReceiveUdpAsync(udp, stopping.Token));
        if (options.Tcp)
        {
            TcpListener tcp = new(options.ListenAddress, UdpEndPoint.Port);
            tcp.Start();
            listeners.Add(tcp.Server);
            TcpEndPoint = (IPEndPoint)tcp.LocalEndpoint;
            listening.Add($"tcp {TcpEndPoint}");
            loops.Add(AcceptAsync(tcp, tls: false, stopping.Token));
        }
        if (options.TlsPort is int tlsPort)
        {
            TcpListener tls = new(options.ListenAddress, tlsPort);
            tls.Start();
            listeners.Add(tls.Server);
            TlsEndPoint = (IPEndPoint)tls.LocalEndpoint;
            listening.Add($"tls {TlsEndPoint}");
            loops.Add(AcceptAsync(tls, tls: true, stopping.Token));
        }

        // Expiry, then the log
        loops.Add(SweepAsync(stopping.Token));
        logger.LogInformation("TURN server listening on {Listening}, relaying from {External} ports {Min}-{Max}", string.Join(", ", listening), externalAddress, options.MinRelayPort, options.MaxRelayPort);
        // Done
        return Task.CompletedTask;
    }

    /// <summary>Stop listening and end every allocation.</summary>
    public async ValueTask DisposeAsync()
    {
        // Once
        if (stopping.IsCancellationRequested) return;
        await stopping.CancelAsync();
        foreach (IDisposable listener in listeners) listener.Dispose();
        foreach (Allocation allocation in allocations.Values) Remove(allocation, "server stopping");
        await Task.WhenAll(loops).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stopping.Dispose();
    }

    /// <summary>Read datagrams of the UDP listener until stopped.</summary>
    private async Task ReceiveUdpAsync(Socket socket, CancellationToken cancellationToken)
    {
        // One buffer, as each datagram is handled before the next is read
        byte[] buffer = new byte[MaxDatagram];
        IPEndPoint local = (IPEndPoint)socket.LocalEndPoint!;
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
            }
            catch (SocketException)
            {
                // E.g. an ICMP unreachable of an earlier send; keep reading
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // On stop
                return;
            }

            // The datagram, with errors logged, not ending the loop
            try
            {
                UdpConnection client = new(socket, (IPEndPoint)received.RemoteEndPoint, local);
                await HandleAsync(client, buffer.AsMemory(0, received.ReceivedBytes), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Handling a datagram from {Client} failed", received.RemoteEndPoint);
            }
        }
    }

    /// <summary>Accept TCP or TLS connections until stopped, serving each on its own.</summary>
    private async Task AcceptAsync(TcpListener listener, bool tls, CancellationToken cancellationToken)
    {
        // Each connection served in the background
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // On stop
                return;
            }
            catch (SocketException)
            {
                // A connection that failed before it was accepted
                continue;
            }
            _ = ServeConnectionAsync(client, tls, cancellationToken);
        }
    }

    /// <summary>Serve one TCP or TLS connection: frame its STUN messages and channel data, until it closes; its allocation ends with it.</summary>
    private async Task ServeConnectionAsync(TcpClient tcp, bool tls, CancellationToken cancellationToken)
    {
        // TLS first when asked, with a short limit for the handshake
        using TcpClient owned = tcp;
        IPEndPoint remote = (IPEndPoint)tcp.Client.RemoteEndPoint!, local = (IPEndPoint)tcp.Client.LocalEndPoint!;
        tcp.NoDelay = true;
        Stream stream = tcp.GetStream();
        StreamConnection? client = null;
        try
        {
            if (tls)
            {
                SslStream ssl = new(stream, leaveInnerStreamOpen: false);
                using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = options.Certificate!() }, handshake.Token);
                stream = ssl;
            }
            client = new StreamConnection(tls ? TurnTransport.Tls : TurnTransport.Tcp, stream, remote, local);

            // Frames: STUN messages and padded channel data, back to back
            byte[] buffer = new byte[MaxDatagram + 8];
            int filled = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                if (read == 0) break;
                filled += read;
                while (ChannelData.FrameLength(buffer.AsSpan(0, filled)) is int length)
                {
                    if (length > buffer.Length) return;
                    if (filled < length) break;
                    await HandleAsync(client, buffer.AsMemory(0, length), cancellationToken);
                    Buffer.BlockCopy(buffer, length, buffer, 0, filled - length);
                    filled -= length;
                }
                if (filled >= 4 && ChannelData.FrameLength(buffer.AsSpan(0, filled)) is null) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException or SocketException)
        {
            // Closed, stopped or a failed handshake
            if (ex is System.Security.Authentication.AuthenticationException) logger.LogDebug("TLS handshake with {Client} failed: {Error}", remote, ex.Message);
        }
        finally
        {
            // The allocation of a stream ends with it
            client?.Close();
            if (client is not null && allocations.TryGetValue(client.Key, out Allocation? allocation)) Remove(allocation, "connection closed");
            await stream.DisposeAsync();
        }
    }

    /// <summary>Read what peers send to an allocation's relay socket, and pass it to its client, until the allocation ends.</summary>
    private async Task RelayFromPeersAsync(Allocation allocation)
    {
        // Peers' datagrams; those without a permission are dropped
        byte[] buffer = new byte[MaxDatagram];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        CancellationToken cancellationToken = allocation.Stopping.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await allocation.Relay.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
            }
            catch (SocketException)
            {
                // E.g. an ICMP unreachable of an earlier send
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // On the allocation's end
                return;
            }
            await DeliverFromPeerAsync(allocation, ClientConnection.Normalize((IPEndPoint)received.RemoteEndPoint), buffer.AsMemory(0, received.ReceivedBytes), cancellationToken);
        }
    }

    /// <summary>Pass data from <paramref name="peer"/> to the allocation's client, on the peer's channel when bound, else in a Data indication; drop it without a permission.</summary>
    private async Task DeliverFromPeerAsync(Allocation allocation, IPEndPoint peer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // Permission and channel, read under the lock
        DateTimeOffset now = time.GetUtcNow();
        ushort? channel;
        lock (allocation.Gate)
        {
            if (!allocation.IsPermitted(peer.Address, now)) return;
            channel = allocation.ChannelOf(peer, now);
        }

        // Channel data, or an indication
        byte[] message;
        if (channel is ushort number) message = ChannelData.Encode(number, data.Span, padded: allocation.Client.IsStream);
        else
        {
            StunMessage indication = new(StunMethod.Data, StunClass.Indication);
            indication.Add(StunAttributeTypes.XorPeerAddress, StunAddress.EncodeXor(peer, indication.TransactionId)).Add(StunAttributeTypes.Data, data.ToArray());
            message = indication.Encode();
        }
        Count(allocation, data.Length);
        // Done
        await allocation.Client.SendAsync(message, cancellationToken);
    }

    /// <summary>Send data from an allocation's client to <paramref name="peer"/>: to another allocation of this server directly, else from the relay socket. The caller has checked the permission.</summary>
    private async Task SendToPeerAsync(Allocation allocation, IPEndPoint peer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // Another allocation here: no round trip through the router
        Count(allocation, data.Length);
        if (IsOwnRelay(peer) && allocationsByPort.TryGetValue(peer.Port, out Allocation? other))
        {
            await DeliverFromPeerAsync(other, allocation.RelayedAddress, data, cancellationToken);
            return;
        }

        // Out of the relay socket
        try
        {
            await allocation.Relay.SendToAsync(data, SocketFlags.None, peer, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Dropped, as UDP may
        }
    }

    /// <summary>Return whether <paramref name="peer"/> is a relayed address of this server.</summary>
    private bool IsOwnRelay(IPEndPoint peer) => peer.Address.Equals(externalAddress) && peer.Port >= options.MinRelayPort && peer.Port <= options.MaxRelayPort;

    /// <summary>Return whether the server relays to <paramref name="peer"/>: its own relayed addresses, allowed ranges, and what denied ranges leave.</summary>
    private bool MayRelayTo(IPAddress peer)
    {
        // Own relays, then the lists
        if (peer.Equals(externalAddress)) return true;
        if (options.AllowedPeers.Any(r => r.Contains(peer))) return true;
        // Return whether not denied
        return !options.DeniedPeers.Any(r => r.Contains(peer));
    }

    /// <summary>Add relayed bytes to the counts.</summary>
    private void Count(Allocation allocation, int bytes)
    {
        // Both counters
        Interlocked.Add(ref allocation.BytesRelayed, bytes);
        Interlocked.Add(ref bytesRelayed, bytes);
    }

    /// <summary>Open a relay socket on a free port of the range; return null when none is free after some tries.</summary>
    private Socket? OpenRelay()
    {
        // Random ports, so they are hard to guess
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int port = Random.Shared.Next(options.MinRelayPort, options.MaxRelayPort + 1);
            if (allocationsByPort.ContainsKey(port)) continue;
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(relayAddress, port));
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }
        // On none free
        return null;
    }

    /// <summary>Start relaying for a new allocation, and count it.</summary>
    private void Add(Allocation allocation)
    {
        // Indexes, then the relay loop
        allocations[allocation.Client.Key] = allocation;
        allocationsByPort[allocation.RelayedAddress.Port] = allocation;
        Interlocked.Increment(ref allocationsMade);
        _ = RelayFromPeersAsync(allocation);
        logger.LogInformation("Allocated {Relayed} for {User} at {Client} over {Transport}", allocation.RelayedAddress, allocation.Username, allocation.Client.Remote, allocation.Client.Transport);
    }

    /// <summary>End an allocation: stop relaying and free its port.</summary>
    private void Remove(Allocation allocation, string reason)
    {
        // Only once
        if (!allocations.TryRemove(new KeyValuePair<string, Allocation>(allocation.Client.Key, allocation))) return;
        allocationsByPort.TryRemove(new KeyValuePair<int, Allocation>(allocation.RelayedAddress.Port, allocation));
        allocation.Stopping.Cancel();
        allocation.Relay.Dispose();
        logger.LogInformation("Ended {Relayed} of {User} ({Reason}); {Bytes} bytes relayed", allocation.RelayedAddress, allocation.Username, reason, Interlocked.Read(ref allocation.BytesRelayed));
    }

    /// <summary>Remove expired allocations, permissions and channels, every few seconds until stopped.</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // A timer on the server's clock
        using PeriodicTimer timer = new(SweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) Sweep();
        }
        catch (OperationCanceledException)
        {
            // On stop
        }
    }

    /// <summary>Remove what has expired now.</summary>
    internal void Sweep()
    {
        // Allocations, then what is inside the others
        DateTimeOffset now = time.GetUtcNow();
        foreach (Allocation allocation in allocations.Values)
        {
            bool expired;
            lock (allocation.Gate)
            {
                expired = allocation.Expires <= now;
                if (!expired) allocation.RemoveExpired(now);
            }
            if (expired) Remove(allocation, "expired");
        }
    }
}
