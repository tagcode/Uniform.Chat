namespace Uniform.Matrix.WebRtc.Tests;

using System.Net;
using System.Threading.Channels;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>Tests of <see cref="IceCandidate"/> and <see cref="IceAgent"/>.</summary>
public class IceTests
{
    /// <summary>Parse candidates as browsers write them, and write them back.</summary>
    [Fact]
    public void ParseCandidates()
    {
        // Server-reflexive with extensions
        IceCandidate candidate = IceCandidate.Parse("candidate:842163049 1 udp 1677729535 198.51.100.7 54321 typ srflx raddr 10.0.0.5 rport 54321 generation 0 ufrag Ab1c network-cost 999")!;
        Assert.Equal("842163049", candidate.Foundation);
        Assert.Equal(1677729535u, candidate.Priority);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 54321), candidate.Address);
        Assert.Equal(IceCandidateType.ServerReflexive, candidate.Type);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 54321), candidate.RelatedAddress);
        Assert.Equal("candidate:842163049 1 udp 1677729535 198.51.100.7 54321 typ srflx raddr 10.0.0.5 rport 54321", candidate.ToString());

        // Not usable: mDNS names and TCP
        Assert.Null(IceCandidate.Parse("candidate:1 1 udp 2122260223 4a2b3c4d-1111-2222-3333-444455556666.local 50000 typ host"));
        Assert.Null(IceCandidate.Parse("candidate:2 1 tcp 1518280447 192.0.2.1 9 typ host tcptype active"));
        // Done
        Assert.Equal(2130706431u, IceCandidate.ComputePriority(IceCandidateType.Host));
    }

    /// <summary>Parse the server URIs of Matrix's <c>/voip/turnServer</c>, skipping those not over UDP.</summary>
    [Fact]
    public void ParseServers()
    {
        // UDP and default ports
        Assert.Equal((true, "turn.example.org", 3478), IceAgent.ParseServer("turn:turn.example.org:3478?transport=udp"));
        Assert.Equal((false, "stun.example.org", 3478), IceAgent.ParseServer("stun:stun.example.org"));
        Assert.Null(IceAgent.ParseServer("turn:turn.example.org:3478?transport=tcp"));
        // Done
        Assert.Null(IceAgent.ParseServer("turns:turn.example.org:5349?transport=tcp"));
    }

    /// <summary>Connect two agents on this machine, and send both ways.</summary>
    [Fact]
    public async Task ConnectDirectly()
    {
        // Gathered, exchanged and connected
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using IceAgent caller = new(new IceOptions { Controlling = true, IncludeLoopback = true });
        await using IceAgent callee = new(new IceOptions { IncludeLoopback = true });
        await ConnectAsync(caller, callee, cancellationToken);
        Assert.Equal(IceCandidateType.Host, caller.SelectedLocal!.Type);
        // Done
        await AssertExchangeAsync(caller, callee, cancellationToken);
    }

    /// <summary>Connect two agents that both start as controlling; the tie-breakers settle it.</summary>
    [Fact]
    public async Task SolveRoleConflict()
    {
        // Both controlling
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using IceAgent first = new(new IceOptions { Controlling = true, IncludeLoopback = true });
        await using IceAgent second = new(new IceOptions { Controlling = true, IncludeLoopback = true });
        await ConnectAsync(first, second, cancellationToken);
        // Done
        Assert.NotEqual(first.Controlling, second.Controlling);
        await AssertExchangeAsync(first, second, cancellationToken);
    }

    /// <summary>Connect two agents through TURN only, each on its own fake server.</summary>
    [Fact]
    public async Task ConnectThroughRelays()
    {
        // A server for each, and only relayed candidates
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using FakeTurnServer callerServer = new("user", "pass"), calleeServer = new("user", "pass");
        IceOptions Options(FakeTurnServer server, bool controlling) => new() { Controlling = controlling, IncludeLoopback = true, RelayOnly = true, Servers = [new IceServer($"turn:127.0.0.1:{server.Address.Port}?transport=udp", "user", "pass")] };
        await using IceAgent caller = new(Options(callerServer, true));
        await using IceAgent callee = new(Options(calleeServer, false));
        await ConnectAsync(caller, callee, cancellationToken);

        // Relayed both ways
        Assert.Equal(IceCandidateType.Relayed, caller.SelectedLocal!.Type);
        Assert.Equal(calleeServer.RelayAddress, caller.SelectedRemote!.Address);
        // Done
        await AssertExchangeAsync(caller, callee, cancellationToken);
    }

    /// <summary>Gather both, exchange credentials and candidates, and connect both.</summary>
    private static async Task ConnectAsync(IceAgent first, IceAgent second, CancellationToken cancellationToken)
    {
        // Credentials and candidates both ways
        await Task.WhenAll(first.GatherAsync(cancellationToken), second.GatherAsync(cancellationToken));
        first.SetRemoteCredentials(second.LocalUfrag, second.LocalPassword);
        second.SetRemoteCredentials(first.LocalUfrag, first.LocalPassword);
        foreach (IceCandidate candidate in second.LocalCandidates) first.AddRemoteCandidate(IceCandidate.Parse(candidate.ToString())!);
        foreach (IceCandidate candidate in first.LocalCandidates) second.AddRemoteCandidate(IceCandidate.Parse(candidate.ToString())!);
        bool[] connected = await Task.WhenAll(first.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken), second.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken));
        // Done
        Assert.All(connected, Assert.True);
    }

    /// <summary>Assert that a datagram goes each way over the selected pairs.</summary>
    private static async Task AssertExchangeAsync(IceAgent first, IceAgent second, CancellationToken cancellationToken)
    {
        // What each receives
        Channel<byte[]> toFirst = Channel.CreateUnbounded<byte[]>(), toSecond = Channel.CreateUnbounded<byte[]>();
        first.DataReceived += data => toFirst.Writer.TryWrite(data);
        second.DataReceived += data => toSecond.Writer.TryWrite(data);

        // A DTLS-looking datagram each way
        await first.SendAsync(new byte[] { 22, 1, 2, 3 }, cancellationToken);
        Assert.Equal(new byte[] { 22, 1, 2, 3 }, await toSecond.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        await second.SendAsync(new byte[] { 23, 4, 5 }, cancellationToken);
        // Done
        Assert.Equal(new byte[] { 23, 4, 5 }, await toFirst.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }
}
