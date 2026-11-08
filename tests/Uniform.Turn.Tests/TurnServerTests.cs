namespace Uniform.Turn.Tests;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Uniform.Stun.Protocol;

/// <summary>Tests of <see cref="TurnServer"/> on loopback, through <see cref="Uniform.Stun.TurnClient"/>.</summary>
public class TurnServerTests
{
    private const string Secret = "shared secret";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Return a started server on loopback with time-limited credentials of <see cref="Secret"/>; relaying to loopback allowed unless <paramref name="configure"/> says otherwise.</summary>
    private static async Task<TurnServer> StartAsync(Action<TurnServerOptions>? configure = null, TimeProvider? time = null)
    {
        // Loopback, any free port, a narrow relay range
        TurnServerOptions options = new() { Realm = "test", ListenAddress = IPAddress.Loopback, Port = 0, MinRelayPort = 40000, MaxRelayPort = 44999, DeniedPeers = [] };
        configure?.Invoke(options);
        TurnServer server = new(options, new SharedSecretAuthenticator([Secret], time ?? TimeProvider.System), NullLogger<TurnServer>.Instance, time);
        await server.StartAsync(Ct);
        // Return server
        return server;
    }

    /// <summary>Return credentials of <see cref="Secret"/> valid for ten minutes.</summary>
    private static (string Username, string Password) Credentials(string name = "@alice:example.org") => TurnCredentials.Create(Secret, name, DateTimeOffset.UtcNow.AddMinutes(10));

    /// <summary>A Binding request gets the client's address, without credentials.</summary>
    [Fact]
    public async Task AnswerBinding()
    {
        // Arrange
        await using TurnServer server = await StartAsync();
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, "", "", cancellationToken: Ct);

        // Act
        StunMessage? response = await client.Stun.RequestAsync(new StunMessage(StunMethod.Binding, StunClass.Request), server.UdpEndPoint!, cancellationToken: Ct);

        // Assert
        Assert.Equal(StunClass.Success, response?.Class);
        IPEndPoint? mapped = StunAddress.DecodeXor(response!.Get(StunAttributeTypes.XorMappedAddress)!, response.TransactionId);
        Assert.Equal(IPAddress.Loopback, mapped?.Address);
    }

    /// <summary>Data goes between a client and a peer both ways: in indications before a channel, then on the channel; the peer sees the relayed address.</summary>
    [Theory]
    [InlineData(TurnTransport.Udp)]
    [InlineData(TurnTransport.Tcp)]
    [InlineData(TurnTransport.Tls)]
    public async Task RelayToPeer(TurnTransport transport)
    {
        // Arrange
        using X509Certificate2 certificate = CreateCertificate();
        await using TurnServer server = await StartAsync(o => { o.TlsPort = 0; o.Certificate = () => certificate; });
        IPEndPoint address = transport switch { TurnTransport.Udp => server.UdpEndPoint!, TurnTransport.Tcp => server.TcpEndPoint!, _ => server.TlsEndPoint! };
        (string username, string password) = Credentials();
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(address, username, password, transport, Ct);
        using UdpClient peer = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint peerAddress = (IPEndPoint)peer.Client.LocalEndPoint!;

        // Act
        Assert.True(await client.Turn.AllocateAsync(Ct));
        IPEndPoint relayed = client.Turn.RelayedAddress!;
        Assert.True(await client.Turn.PermitAsync(peerAddress.Address, Ct));
        await AssertBothWaysAsync(client, peer, peerAddress, relayed, "indication");
        Assert.True(await client.Turn.BindChannelAsync(peerAddress, Ct));
        await AssertBothWaysAsync(client, peer, peerAddress, relayed, "channel");

        // Assert
        Assert.Equal(1, server.Status.Allocations);
        Assert.True(server.Status.BytesRelayed > 0);
    }

    /// <summary>Two clients of the server reach each other's relayed addresses, delivered inside the server.</summary>
    [Fact]
    public async Task RelayBetweenAllocations()
    {
        // Arrange
        await using TurnServer server = await StartAsync(o => o.DeniedPeers = IpRange.NonPublic);
        (string username, string password) = Credentials();
        await using TurnClientEndpoint first = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);
        await using TurnClientEndpoint second = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);
        Assert.True(await first.Turn.AllocateAsync(Ct));
        Assert.True(await second.Turn.AllocateAsync(Ct));
        IPEndPoint firstRelay = first.Turn.RelayedAddress!, secondRelay = second.Turn.RelayedAddress!;

        // Act
        Assert.True(await first.Turn.PermitAsync(secondRelay.Address, Ct));
        Assert.True(await second.Turn.BindChannelAsync(firstRelay, Ct));
        await first.Turn.SendAsync("hello"u8.ToArray(), secondRelay, Ct);
        (IPEndPoint from, byte[] data) = await second.Received.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await second.Turn.SendAsync("back"u8.ToArray(), firstRelay, Ct);
        (IPEndPoint backFrom, byte[] back) = await first.Received.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Assert
        Assert.Equal(firstRelay, from);
        Assert.Equal("hello"u8.ToArray(), data);
        Assert.Equal(secondRelay, backFrom);
        Assert.Equal("back"u8.ToArray(), back);
    }

    /// <summary>Wrong passwords, expired credentials and other secrets get no allocation.</summary>
    [Fact]
    public async Task RefuseBadCredentials()
    {
        // Arrange
        await using TurnServer server = await StartAsync();
        (string username, _) = Credentials();
        (string expired, string expiredPassword) = TurnCredentials.Create(Secret, "@alice:example.org", DateTimeOffset.UtcNow.AddMinutes(-1));
        (string other, string otherPassword) = TurnCredentials.Create("another secret", "@alice:example.org", DateTimeOffset.UtcNow.AddMinutes(10));

        // Act and assert
        foreach ((string user, string password) in new[] { (username, "wrong"), (expired, expiredPassword), (other, otherPassword) })
        {
            await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, user, password, cancellationToken: Ct);
            Assert.False(await client.Turn.AllocateAsync(Ct));
        }
        Assert.Equal(0, server.Status.Allocations);
    }

    /// <summary>By default the server refuses private and loopback peers but permits public ones and its own relayed addresses.</summary>
    [Fact]
    public async Task DenyPrivatePeers()
    {
        // Arrange
        await using TurnServer server = await StartAsync(o => o.DeniedPeers = IpRange.NonPublic);
        (string username, string password) = Credentials();
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);
        Assert.True(await client.Turn.AllocateAsync(Ct));

        // Act and assert
        Assert.False(await client.Turn.PermitAsync(IPAddress.Parse("192.168.1.10"), Ct));
        Assert.False(await client.Turn.PermitAsync(IPAddress.Parse("10.0.0.1"), Ct));
        Assert.False(await client.Turn.BindChannelAsync(new IPEndPoint(IPAddress.Parse("169.254.1.1"), 5000), Ct));
        Assert.True(await client.Turn.PermitAsync(IPAddress.Parse("203.0.114.7"), Ct));
        Assert.True(await client.Turn.PermitAsync(client.Turn.RelayedAddress!.Address, Ct));
    }

    /// <summary>A user gets at most its quota of allocations; one more is refused until one ends.</summary>
    [Fact]
    public async Task LimitAllocationsPerUser()
    {
        // Arrange
        await using TurnServer server = await StartAsync(o => o.MaxAllocationsPerUser = 1);
        (string username, string password) = Credentials();
        TurnClientEndpoint first = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);
        await using TurnClientEndpoint second = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);

        // Act and assert
        Assert.True(await first.Turn.AllocateAsync(Ct));
        Assert.False(await second.Turn.AllocateAsync(Ct));
        await first.DisposeAsync();
        Assert.Equal(0, server.Status.Allocations);
        Assert.True(await second.Turn.AllocateAsync(Ct));
    }

    /// <summary>An allocation ends when its lifetime passes without a refresh.</summary>
    [Fact]
    public async Task ExpireAllocations()
    {
        // Arrange
        FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        await using TurnServer server = await StartAsync(time: time);
        (string username, string password) = Credentials();
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, username, password, cancellationToken: Ct);
        Assert.True(await client.Turn.AllocateAsync(Ct));

        // Act
        time.Advance(TimeSpan.FromMinutes(9));
        server.Sweep();
        int before = server.Status.Allocations;
        time.Advance(TimeSpan.FromMinutes(2));
        server.Sweep();

        // Assert
        Assert.Equal(1, before);
        Assert.Equal(0, server.Status.Allocations);
    }

    /// <summary>A request with a comprehension-required attribute the server does not know gets 420 with that attribute listed.</summary>
    [Fact]
    public async Task RefuseUnknownAttributes()
    {
        // Arrange
        await using TurnServer server = await StartAsync();
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(server.UdpEndPoint!, "", "", cancellationToken: Ct);
        StunMessage request = new StunMessage(StunMethod.Allocate, StunClass.Request).Add(StunAttributeTypes.RequestedTransport, [17, 0, 0, 0]).Add(StunAttributeTypes.EvenPort, [0x80]);

        // Act
        StunMessage? response = await client.Stun.RequestAsync(request, server.UdpEndPoint!, cancellationToken: Ct);

        // Assert
        Assert.Equal(StunErrorCodes.UnknownAttribute, response?.ErrorCode);
        Assert.Equal([0x00, 0x18], response!.Get(StunAttributeTypes.UnknownAttributes));
    }

    /// <summary>Address ranges match addresses by their prefix, IPv4 mapped into IPv6 as IPv4.</summary>
    [Fact]
    public void MatchAddressRanges()
    {
        // Arrange
        IpRange privateRange = IpRange.Parse("172.16.0.0/12");

        // Act and assert
        Assert.True(privateRange.Contains(IPAddress.Parse("172.31.255.1")));
        Assert.False(privateRange.Contains(IPAddress.Parse("172.32.0.1")));
        Assert.True(privateRange.Contains(IPAddress.Parse("::ffff:172.20.0.5")));
        Assert.Contains(IpRange.NonPublic, r => r.Contains(IPAddress.Parse("fe80::1")));
        Assert.DoesNotContain(IpRange.NonPublic, r => r.Contains(IPAddress.Parse("8.8.8.8")));
        Assert.Throws<FormatException>(() => IpRange.Parse("10.0.0.0/33"));
    }

    /// <summary>Assert that data goes from the client to the peer through the relay, and back.</summary>
    private static async Task AssertBothWaysAsync(TurnClientEndpoint client, UdpClient peer, IPEndPoint peerAddress, IPEndPoint relayed, string text)
    {
        // Out through the relay
        await client.Turn.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), peerAddress, Ct);
        UdpReceiveResult received = await peer.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(received.Buffer));
        Assert.Equal(relayed, received.RemoteEndPoint);

        // Back from the peer
        await peer.SendAsync(System.Text.Encoding.UTF8.GetBytes(text + " back"), relayed, Ct);
        (IPEndPoint from, byte[] data) = await client.Received.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // Done
        Assert.Equal(peerAddress, from);
        Assert.Equal(text + " back", System.Text.Encoding.UTF8.GetString(data));
    }

    /// <summary>Return a self-signed certificate for localhost with its key, usable by SslStream.</summary>
    private static X509Certificate2 CreateCertificate()
    {
        // ECDSA, a day long
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Return it through PKCS #12, as SslStream on Linux needs a persisted key
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
