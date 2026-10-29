namespace Uniform.Matrix.WebRtc.Ice;

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Uniform.Matrix.WebRtc.Stun;
using Uniform.Matrix.WebRtc.Turn;

/// <summary>A STUN or TURN server, as Matrix's <c>/voip/turnServer</c> lists them.</summary>
/// <param name="Uri">URI such as <c>turn:turn.example.org:3478?transport=udp</c> or <c>stun:stun.example.org</c>; only UDP is used.</param>
/// <param name="Username">TURN username.</param>
/// <param name="Password">TURN password.</param>
public sealed record IceServer(string Uri, string? Username = null, string? Password = null);

/// <summary>Options of an <see cref="IceAgent"/>.</summary>
public sealed record IceOptions
{
    /// <summary>Whether this agent starts as the controlling one, which nominates the pair; the caller is.</summary>
    public bool Controlling { get; init; }
    /// <summary>STUN and TURN servers to gather reflexive and relayed candidates from.</summary>
    public IReadOnlyList<IceServer> Servers { get; init; } = [];
    /// <summary>Whether to offer and pair loopback addresses, e.g. for tests on one machine.</summary>
    public bool IncludeLoopback { get; init; }
    /// <summary>Whether to offer and pair only relayed candidates, so the call goes through TURN; hides the addresses from the peer.</summary>
    public bool RelayOnly { get; init; }
    /// <summary>Whether to gather and pair IPv6 host candidates, where the system has IPv6.</summary>
    public bool EnableIPv6 { get; init; } = true;
    /// <summary>How long to wait for each server while gathering.</summary>
    public TimeSpan ServerTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>The state of an <see cref="IceAgent"/>.</summary>
public enum IceState
{
    /// <summary>Not yet checking.</summary>
    New,
    /// <summary>Checking candidate pairs.</summary>
    Checking,
    /// <summary>A pair is selected, and the peer answers it.</summary>
    Connected,
    /// <summary>The peer stopped answering the selected pair; it may come back.</summary>
    Disconnected,
    /// <summary>No pair worked.</summary>
    Failed,
    /// <summary>Closed.</summary>
    Closed,
}

/// <summary>A full <see href="https://www.rfc-editor.org/rfc/rfc8445">ICE</see> agent for one component over UDP, IPv4 and IPv6, as WebRTC's bundled, RTCP-multiplexed media needs: gathers candidates, checks pairs with the peer, selects one, and carries datagrams over it.</summary>
/// <remarks>
/// One IPv4 socket bound to all interfaces is the base of every IPv4 host candidate, and of the server-reflexive ones; each TURN allocation on that socket is the base of a relayed candidate. An IPv6 socket is likewise the base of the IPv6 host candidates, which are preferred, as <see href="https://www.rfc-editor.org/rfc/rfc8421">RFC 8421</see> recommends. So pairs are bases times remote candidates of the same family; relays, being IPv4, pair with IPv4 peers.
/// Candidates trickle: <see cref="CandidateGathered"/> raises each as found, and <see cref="AddRemoteCandidate"/> takes the peer's at any time. The controlling agent nominates the best pair that works, shortly after the first one does; checks then continue every few seconds, for <see href="https://www.rfc-editor.org/rfc/rfc7675">consent</see>.
/// Datagrams that are not STUN, i.e. DTLS and SRTP, go to <see cref="DataReceived"/>.
/// </remarks>
public sealed class IceAgent : IAsyncDisposable
{
    /// <summary>Pacing of new checks.</summary>
    private static readonly TimeSpan Ta = TimeSpan.FromMilliseconds(20);
    /// <summary>How long the controlling agent waits for better pairs after the first works.</summary>
    private static readonly TimeSpan NominationDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Interval of consent checks on the selected pair.</summary>
    private static readonly TimeSpan ConsentInterval = TimeSpan.FromSeconds(2);
    /// <summary>How long without consent counts as disconnected, e.g. after the network changed; short, so that a call can restart ICE soon.</summary>
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(7);
    private readonly IceOptions options;
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
    private readonly UdpClient? socket6;
    private readonly StunClient directStun;
    private readonly LocalBase host;
    private readonly LocalBase host6;
    private readonly List<LocalBase> relays = [];
    private readonly List<IceCandidate> localCandidates = [];
    private readonly List<IceCandidate> remoteCandidates = [];
    private readonly List<CandidatePair> pairs = [];
    private readonly Lock gate = new();
    private readonly ulong tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
    private readonly byte[] localKey;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<CandidatePair> selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task reading;
    private string? remoteUfrag;
    private byte[]? remoteKey;
    private CandidatePair? selected;
    private DateTime firstSuccess;
    private bool nominating;
    private Task? consent;

    /// <summary>A base of local candidates: the socket itself, or a TURN allocation on it.</summary>
    /// <param name="Stun">Transactions sent from this base.</param>
    /// <param name="Turn">The allocation, for relayed bases.</param>
    private sealed record LocalBase(StunClient Stun, TurnClient? Turn)
    {
        /// <summary>The candidate that ranks pairs of this base.</summary>
        public IceCandidate? Candidate { get; set; }
    }

    /// <summary>A local base and a remote candidate, and how its checks went.</summary>
    private sealed class CandidatePair(LocalBase local, IceCandidate remote)
    {
        public LocalBase Local { get; } = local;
        public IceCandidate Remote { get; } = remote;
        public PairState State { get; set; }
        public bool Nominated { get; set; }
        public DateTime LastConsent { get; set; }

        /// <summary>Return the pair priority of <see href="https://www.rfc-editor.org/rfc/rfc8445#section-6.1.2.3">RFC 8445 6.1.2.3</see>.</summary>
        public ulong Priority(bool controlling)
        {
            // G is the controlling agent's candidate, D the controlled one's
            ulong local = Local.Candidate?.Priority ?? 0, remote = Remote.Priority;
            ulong g = controlling ? local : remote, d = controlling ? remote : local;
            // Return priority
            return (Math.Min(g, d) << 32) + 2 * Math.Max(g, d) + (g > d ? 1UL : 0);
        }
    }

    /// <summary>States of a pair's checks.</summary>
    private enum PairState { Waiting, InProgress, Succeeded, Failed }

    /// <summary>Open the socket, and start reading it.</summary>
    public IceAgent(IceOptions options)
    {
        // Credentials of ice-chars, longer than the minimum of 4 and 22
        this.options = options;
        Controlling = options.Controlling;
        LocalUfrag = RandomIceChars(8);
        LocalPassword = RandomIceChars(24);
        localKey = Encoding.UTF8.GetBytes(LocalPassword);
        directStun = new StunClient(SendDirectAsync);
        host = new LocalBase(directStun, null);
        host6 = new LocalBase(directStun, null);
        socket6 = options.EnableIPv6 ? OpenIPv6() : null;
        reading = Task.WhenAll(ReadAsync(socket, host, stopping.Token), socket6 is null ? Task.CompletedTask : ReadAsync(socket6, host6, stopping.Token));
    }

    /// <summary>This agent's username fragment, for the SDP's <c>a=ice-ufrag</c>.</summary>
    public string LocalUfrag { get; }

    /// <summary>This agent's password, for the SDP's <c>a=ice-pwd</c>.</summary>
    public string LocalPassword { get; }

    /// <summary>Whether this agent is the controlling one now; a role conflict may change it.</summary>
    public bool Controlling { get; private set; }

    /// <summary>The state.</summary>
    public IceState State { get; private set; }

    /// <summary>The candidates gathered so far.</summary>
    public IReadOnlyList<IceCandidate> LocalCandidates { get { lock (gate) return [.. localCandidates]; } }

    /// <summary>The remote candidate of the selected pair, once connected.</summary>
    public IceCandidate? SelectedRemote => selected?.Remote;

    /// <summary>The local candidate of the selected pair, once connected.</summary>
    public IceCandidate? SelectedLocal => selected?.Local.Candidate;

    /// <summary>Raised for each local candidate as it is gathered, to trickle it to the peer.</summary>
    public event Action<IceCandidate>? CandidateGathered;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<IceState>? StateChanged;

    /// <summary>Raised on the read loop for each datagram from the peer that is not STUN, e.g. DTLS or SRTP.</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>Gather host candidates, then reflexive and relayed ones from the servers; return when all servers answered or timed out.</summary>
    public async Task GatherAsync(CancellationToken cancellationToken = default)
    {
        // Host candidates on every interface address and the socket's port; IPv6 preferred
        if (socket6 is not null) AddHostCandidates(socket6, host6, AddressFamily.InterNetworkV6, "h6-", 65535);
        AddHostCandidates(socket, host, AddressFamily.InterNetwork, "h", 49151);

        // Servers in parallel
        IEnumerable<Task> servers = options.Servers.Select((server, index) => GatherFromAsync(server, index, cancellationToken));
        // Done
        await Task.WhenAll(servers);
    }

    /// <summary>Add the host candidates of one socket, the first ranking its base's pairs.</summary>
    /// <param name="udp">The socket.</param>
    /// <param name="hostBase">Its base.</param>
    /// <param name="family">Its address family.</param>
    /// <param name="foundation">Prefix of the candidates' foundations.</param>
    /// <param name="localPreference">Local preference of the first address; the next ones get one less each.</param>
    private void AddHostCandidates(UdpClient udp, LocalBase hostBase, AddressFamily family, string foundation, int localPreference)
    {
        // Each address of the family
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        IPAddress[] addresses = HostAddresses(family);
        for (int i = 0; i < addresses.Length; i++)
        {
            IceCandidate candidate = new() { Foundation = $"{foundation}{i}", Priority = IceCandidate.ComputePriority(IceCandidateType.Host, (ushort)(localPreference - i)), Address = new IPEndPoint(addresses[i], port), Type = IceCandidateType.Host };
            hostBase.Candidate ??= candidate;
            if (!options.RelayOnly) AddLocal(candidate);
        }
        // Done, with a base that ranks pairs even without an address
        hostBase.Candidate ??= new IceCandidate { Foundation = foundation, Priority = IceCandidate.ComputePriority(IceCandidateType.Host, (ushort)localPreference), Address = new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port), Type = IceCandidateType.Host };
    }

    /// <summary>Set the peer's username fragment and password, from its SDP; checks wait for them.</summary>
    public void SetRemoteCredentials(string ufrag, string password)
    {
        // Key of the checks this agent sends
        remoteUfrag = ufrag;
        remoteKey = Encoding.UTF8.GetBytes(password);
    }

    /// <summary>Add a candidate of the peer, and pair it with the local bases of its family; ignored when it is not usable, e.g. IPv6 without an IPv6 socket.</summary>
    public void AddRemoteCandidate(IceCandidate candidate)
    {
        // A family with a socket; loopback only when asked; relayed only when asked
        bool ipv6 = candidate.Address.AddressFamily == AddressFamily.InterNetworkV6;
        if (candidate.Component != 1 || (ipv6 && socket6 is null) || (!ipv6 && candidate.Address.AddressFamily != AddressFamily.InterNetwork)) return;
        if (IPAddress.IsLoopback(candidate.Address.Address) && !options.IncludeLoopback) return;
        LocalBase[] bases;
        lock (gate)
        {
            if (remoteCandidates.Any(c => c.Address.Equals(candidate.Address))) return;
            remoteCandidates.Add(candidate);
            bases = ipv6 ? (options.RelayOnly ? [] : [host6]) : options.RelayOnly ? [.. relays] : [host, .. relays];
            foreach (LocalBase local in bases) pairs.Add(new CandidatePair(local, candidate));
        }

        // Relays may send to it once permitted
        foreach (LocalBase relay in bases.Where(b => b.Turn is not null)) _ = relay.Turn!.PermitAsync(candidate.Address.Address, stopping.Token);
    }

    /// <summary>Check pairs until one is selected; return whether one was before <paramref name="timeout"/>.</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Checks paced by Ta until selected, or the time is up
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        limit.CancelAfter(timeout);
        SetState(IceState.Checking);
        try
        {
            while (!selection.Task.IsCompleted)
            {
                CheckNext(limit.Token);
                if (Controlling) NominateBest(limit.Token);
                await Task.WhenAny(selection.Task, Task.Delay(Ta, limit.Token));
                if (!selection.Task.IsCompleted) limit.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // On timeout
            SetState(IceState.Failed);
            return false;
        }
        // Done
        return true;
    }

    /// <summary>Send a datagram to the peer over the selected pair.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        // Assert connected
        if (selected is not CandidatePair pair) throw new InvalidOperationException("No candidate pair is selected.");
        // Direct or relayed
        return pair.Local.Turn is TurnClient turn ? turn.SendAsync(datagram, pair.Remote.Address, cancellationToken) : SendDirectAsync(datagram, pair.Remote.Address, cancellationToken);
    }

    /// <summary>Stop, release the allocations, and close the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        // Loops first, then the allocations, then the socket
        if (State == IceState.Closed) return;
        await stopping.CancelAsync();
        if (consent is not null) await consent.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (LocalBase relay in relays) await relay.Turn!.DisposeAsync();
        socket.Dispose();
        socket6?.Dispose();
        await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Done
        SetState(IceState.Closed);
    }

    /// <summary>Gather from one server: a server-reflexive candidate from STUN, and also a relayed one from TURN.</summary>
    private async Task GatherFromAsync(IceServer server, int index, CancellationToken cancellationToken)
    {
        // UDP servers only, by IPv4 address
        if (ParseServer(server.Uri) is not (bool isTurn, string name, int port)) return;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.ServerTimeout);
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, limit.Token);
            if (addresses.Length == 0) return;
            IPEndPoint address = new(addresses[0], port);

            // STUN: the mapped address
            if (!isTurn)
            {
                StunMessage? response = await directStun.RequestAsync(new StunMessage(StunMethod.Binding, StunClass.Request), address, cancellationToken: limit.Token);
                if (response?.Get(StunAttributeTypes.XorMappedAddress) is byte[] value && StunAddress.DecodeXor(value, response.TransactionId) is IPEndPoint mapped) AddReflexive(mapped, index);
                return;
            }

            // TURN: the mapped and relayed addresses
            if (server.Username is null || server.Password is null) return;
            TurnClient turn = new(directStun, SendDirectAsync, address, server.Username, server.Password);
            if (!await turn.AllocateAsync(limit.Token) || turn.RelayedAddress is null)
            {
                await turn.DisposeAsync();
                return;
            }
            if (turn.MappedAddress is not null) AddReflexive(turn.MappedAddress, index);
            IceCandidate relayed = new() { Foundation = $"r{index}", Priority = IceCandidate.ComputePriority(IceCandidateType.Relayed, (ushort)(65535 - index)), Address = turn.RelayedAddress, Type = IceCandidateType.Relayed, RelatedAddress = turn.MappedAddress };
            LocalBase relay = new(new StunClient((datagram, to, token) => turn.SendAsync(datagram, to, token)), turn) { Candidate = relayed };
            AddRelay(relay);
            AddLocal(relayed);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException && !cancellationToken.IsCancellationRequested)
        {
            // On an unreachable server, gather without it
        }
    }

    /// <summary>Add a relayed base, paired with the remote candidates there are.</summary>
    private void AddRelay(LocalBase relay)
    {
        // Pairs with the remotes known, which it must permit
        IceCandidate[] remotes;
        lock (gate)
        {
            relays.Add(relay);
            remotes = [.. remoteCandidates.Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork)];
            foreach (IceCandidate remote in remotes) pairs.Add(new CandidatePair(relay, remote));
        }
        // Done
        foreach (IceCandidate remote in remotes) _ = relay.Turn!.PermitAsync(remote.Address.Address, stopping.Token);
    }

    /// <summary>Add a server-reflexive candidate, unless it is a host address already, or only relayed ones are offered.</summary>
    private void AddReflexive(IPEndPoint mapped, int index)
    {
        // Distinct addresses only
        if (options.RelayOnly) return;
        lock (gate) if (localCandidates.Any(c => c.Address.Equals(mapped))) return;
        // Done
        AddLocal(new IceCandidate { Foundation = $"s{index}", Priority = IceCandidate.ComputePriority(IceCandidateType.ServerReflexive, (ushort)(65535 - index)), Address = mapped, Type = IceCandidateType.ServerReflexive, RelatedAddress = host.Candidate?.Address });
    }

    /// <summary>Add a local candidate, and tell about it.</summary>
    private void AddLocal(IceCandidate candidate)
    {
        // Listed, then raised
        lock (gate) localCandidates.Add(candidate);
        // Done
        CandidateGathered?.Invoke(candidate);
    }

    /// <summary>Start a check of the best waiting pair, if any, once the peer's credentials are known.</summary>
    private void CheckNext(CancellationToken cancellationToken)
    {
        // Highest priority first
        if (remoteKey is null) return;
        CandidatePair? next;
        lock (gate) next = pairs.Where(p => p.State == PairState.Waiting).MaxBy(p => p.Priority(Controlling));
        // Done
        if (next is not null) _ = CheckAsync(next, false, cancellationToken);
    }

    /// <summary>Nominate the best pair that works, after waiting a little for better ones, when no nomination is under way.</summary>
    private void NominateBest(CancellationToken cancellationToken)
    {
        // The best succeeded pair, once the wait is over or nothing else is being checked
        CandidatePair? best;
        lock (gate)
        {
            if (nominating || firstSuccess == default) return;
            bool pending = pairs.Any(p => p.State is PairState.Waiting or PairState.InProgress);
            if (pending && DateTime.UtcNow - firstSuccess < NominationDelay) return;
            best = pairs.Where(p => p.State == PairState.Succeeded).MaxBy(p => p.Priority(true));
            if (best is null) return;
            nominating = true;
        }
        // Done
        _ = CheckAsync(best, true, cancellationToken);
    }

    /// <summary>Send a connectivity check on a pair, nominating it when asked; mark how it went, and select it when nominated.</summary>
    private async Task CheckAsync(CandidatePair pair, bool nominate, CancellationToken cancellationToken)
    {
        // Binding with the peer's credentials, role and our priority as peer-reflexive
        if (remoteUfrag is null || remoteKey is null) return;
        if (!nominate) pair.State = PairState.InProgress;
        StunMessage request = new(StunMethod.Binding, StunClass.Request);
        byte[] priority = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(priority, IceCandidate.ComputePriority(IceCandidateType.PeerReflexive, (ushort)((pair.Local.Candidate?.Priority ?? 0) >> 8)));
        byte[] tie = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tie, tieBreaker);
        request.Add(StunAttributeTypes.Username, $"{remoteUfrag}:{LocalUfrag}").Add(StunAttributeTypes.Priority, priority);
        request.Add(Controlling ? StunAttributeTypes.IceControlling : StunAttributeTypes.IceControlled, tie);
        if (nominate) request.Add(StunAttributeTypes.UseCandidate, []);

        // The response decides
        StunMessage? response;
        try { response = await pair.Local.Stun.RequestAsync(request, pair.Remote.Address, remoteKey, true, cancellationToken); }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { response = null; }
        lock (gate)
        {
            if (response?.ErrorCode == 487)
            {
                // Role conflict: switch, and check again
                Controlling = !Controlling;
                pair.State = PairState.Waiting;
                nominating = false;
                return;
            }
            if (response?.Class != StunClass.Success)
            {
                if (nominate) nominating = false;
                else pair.State = PairState.Failed;
                return;
            }
            pair.State = PairState.Succeeded;
            pair.LastConsent = DateTime.UtcNow;
            if (firstSuccess == default) firstSuccess = DateTime.UtcNow;
        }
        // Done
        if (nominate || (!Controlling && pair.Nominated)) Select(pair);
    }

    /// <summary>Answer a connectivity check from the peer, solving role conflicts; learn its address, and select the pair when it nominates one that works.</summary>
    private async Task AnswerCheckAsync(LocalBase local, IPEndPoint from, byte[] datagram, StunMessage request)
    {
        // For this agent, with its password, intact
        if (request.GetText(StunAttributeTypes.Username) is not string username || !username.StartsWith(LocalUfrag + ":", StringComparison.Ordinal)) return;
        if (!StunMessage.CheckIntegrity(datagram, localKey) || !StunMessage.CheckFingerprint(datagram)) return;

        // Role conflict of RFC 8445 7.3.1.1: the larger tie-breaker controls
        byte[]? theirControlling = request.Get(StunAttributeTypes.IceControlling), theirControlled = request.Get(StunAttributeTypes.IceControlled);
        ulong theirs = (theirControlling ?? theirControlled) is { Length: 8 } tie ? BinaryPrimitives.ReadUInt64BigEndian(tie) : 0;
        bool conflict = false;
        lock (gate)
        {
            if (Controlling && theirControlling is not null)
            {
                if (tieBreaker >= theirs) conflict = true;
                else Controlling = false;
            }
            else if (!Controlling && theirControlled is not null)
            {
                if (tieBreaker >= theirs) Controlling = true;
                else conflict = true;
            }
        }

        // Success with the address seen, or the conflict
        StunMessage response = new(StunMethod.Binding, conflict ? StunClass.Error : StunClass.Success, request.TransactionId);
        if (conflict) response.Add(StunAttributeTypes.ErrorCode, [0, 0, 4, 87]);
        else response.Add(StunAttributeTypes.XorMappedAddress, StunAddress.EncodeXor(from, request.TransactionId));
        byte[] encoded = response.Encode(localKey, true);
        if (local.Turn is TurnClient turn) await turn.SendAsync(encoded, from, stopping.Token);
        else await SendDirectAsync(encoded, from, stopping.Token);
        if (conflict) return;

        // The pair, a peer-reflexive remote when unknown, checked back at once
        uint priority = request.Get(StunAttributeTypes.Priority) is { Length: 4 } value ? BinaryPrimitives.ReadUInt32BigEndian(value) : 0;
        CandidatePair pair = FindOrAddPair(local, from, priority);
        bool select;
        lock (gate)
        {
            if (request.Get(StunAttributeTypes.UseCandidate) is not null && !Controlling) pair.Nominated = true;
            select = pair.Nominated && pair.State == PairState.Succeeded;
        }
        if (select) Select(pair);
        // Done
        else if (pair.State is PairState.Waiting or PairState.Failed) _ = CheckAsync(pair, false, stopping.Token);
    }

    /// <summary>Return the pair of a base and a remote address, adding the address as a peer-reflexive candidate when unknown.</summary>
    private CandidatePair FindOrAddPair(LocalBase local, IPEndPoint remote, uint priority)
    {
        // Known pair, or a new one
        lock (gate)
        {
            if (pairs.FirstOrDefault(p => p.Local == local && p.Remote.Address.Equals(remote)) is CandidatePair known) return known;
            IceCandidate candidate = remoteCandidates.FirstOrDefault(c => c.Address.Equals(remote)) ?? new IceCandidate { Foundation = "p", Priority = priority, Address = remote, Type = IceCandidateType.PeerReflexive };
            if (!remoteCandidates.Contains(candidate)) remoteCandidates.Add(candidate);
            CandidatePair pair = new(local, candidate);
            pairs.Add(pair);
            // Return the new pair
            return pair;
        }
    }

    /// <summary>Select a pair: connected, consent checks started, and a channel bound when relayed.</summary>
    private void Select(CandidatePair pair)
    {
        // Once
        lock (gate)
        {
            if (selected is not null) return;
            selected = pair;
        }
        selection.TrySetResult(pair);
        SetState(IceState.Connected);
        if (pair.Local.Turn is TurnClient turn) _ = turn.BindChannelAsync(pair.Remote.Address, stopping.Token);
        // Done
        consent = KeepConsentAsync(pair, stopping.Token);
    }

    /// <summary>Check the selected pair every few seconds; disconnected when it stops answering, connected again when it answers.</summary>
    private async Task KeepConsentAsync(CandidatePair pair, CancellationToken cancellationToken)
    {
        // Checks until stopped
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ConsentInterval, cancellationToken);
                await CheckAsync(pair, false, cancellationToken);
                bool fresh = DateTime.UtcNow - pair.LastConsent < ConsentTimeout;
                if (!fresh && State == IceState.Connected) SetState(IceState.Disconnected);
                if (fresh && State == IceState.Disconnected) SetState(IceState.Connected);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed
        }
    }

    /// <summary>Read a socket until closed: TURN's responses and relayed data, the peer's checks and responses, and the rest to <see cref="DataReceived"/>.</summary>
    /// <param name="udp">The socket.</param>
    /// <param name="hostBase">The base of its host candidates.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    private async Task ReadAsync(UdpClient udp, LocalBase hostBase, CancellationToken cancellationToken)
    {
        // Each datagram
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(cancellationToken); }
            catch (SocketException) { continue; }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            IPEndPoint from = result.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(result.RemoteEndPoint.Address.MapToIPv4(), result.RemoteEndPoint.Port) : result.RemoteEndPoint;
            byte[] datagram = result.Buffer;

            // Responses to this socket's transactions, direct checks and TURN alike
            if (StunMessage.IsStun(datagram) && directStun.Receive(datagram)) continue;

            // What a relay unwraps came from the peer through it
            LocalBase local = hostBase;
            LocalBase[] relaysNow;
            lock (gate) relaysNow = [.. relays];
            foreach (LocalBase relay in relaysNow)
            {
                if (!relay.Turn!.TryUnwrap(datagram, from, out IPEndPoint peer, out byte[] data)) continue;
                local = relay;
                from = peer;
                datagram = data;
                break;
            }
            if (local == hostBase && relaysNow.Any(r => r.Turn!.Server.Equals(from))) continue;
            if (local == hostBase && options.RelayOnly) continue;
            // Done
            Dispatch(local, from, datagram);
        }
    }

    /// <summary>Handle a datagram from the peer received on a base.</summary>
    private void Dispatch(LocalBase local, IPEndPoint from, byte[] datagram)
    {
        // Media and DTLS
        if (!StunMessage.IsStun(datagram))
        {
            DataReceived?.Invoke(datagram);
            return;
        }

        // Responses on a relay, then the peer's checks
        if (local.Stun.Receive(datagram)) return;
        if (StunMessage.Parse(datagram) is not { Method: StunMethod.Binding, Class: StunClass.Request } request) return;
        // Done
        _ = AnswerCheckAsync(local, from, datagram, request);
    }

    /// <summary>Send a datagram from the socket of the destination's family.</summary>
    private async ValueTask SendDirectAsync(ReadOnlyMemory<byte> datagram, IPEndPoint to, CancellationToken cancellationToken)
    {
        // Unreachable addresses fail the checks, not the agent
        UdpClient? udp = to.AddressFamily == AddressFamily.InterNetworkV6 ? socket6 : socket;
        if (udp is null) return;
        try { await udp.SendAsync(datagram, to, cancellationToken); }
        catch (SocketException) { }
    }

    /// <summary>Return an IPv6-only socket on any address, or null where the system has no IPv6.</summary>
    private static UdpClient? OpenIPv6()
    {
        // Not dual mode: IPv4 has its own socket
        if (!Socket.OSSupportsIPv6) return null;
        try
        {
            UdpClient udp = new(AddressFamily.InterNetworkV6);
            udp.Client.DualMode = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return udp;
        }
        catch (SocketException)
        {
            // On IPv6 turned off
            return null;
        }
    }

    /// <summary>Set the state, and tell when it changed.</summary>
    private void SetState(IceState state)
    {
        // Changes only
        if (State == state) return;
        State = state;
        // Done
        StateChanged?.Invoke(state);
    }

    /// <summary>Return the addresses of a family on the interfaces that are up; loopback only when asked, link-local never.</summary>
    private IPAddress[] HostAddresses(AddressFamily family)
    {
        // Unicast addresses of up interfaces, without IPv4's 169.254/16 and IPv6's fe80::/10 and site-local
        IEnumerable<IPAddress> addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == family && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        // Return them, loopback last
        return [.. addresses.Where(a => options.IncludeLoopback || !IPAddress.IsLoopback(a)).OrderBy(IPAddress.IsLoopback).Distinct()];
    }

    /// <summary>Return whether a server URI is TURN, its host and port; null when it is not over UDP.</summary>
    internal static (bool IsTurn, string Host, int Port)? ParseServer(string uri)
    {
        // Scheme, then host and port before any query
        bool isTurn = uri.StartsWith("turn:", StringComparison.OrdinalIgnoreCase);
        if (!isTurn && !uri.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = uri[5..];
        int query = rest.IndexOf('?');
        if (query >= 0)
        {
            if (!rest[query..].Contains("transport=udp", StringComparison.OrdinalIgnoreCase) && rest[query..].Contains("transport=", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest[..query];
        }
        int colon = rest.LastIndexOf(':');
        if (colon < 0) return (isTurn, rest, 3478);
        // Return the parts
        return int.TryParse(rest[(colon + 1)..], out int port) ? (isTurn, rest[..colon], port) : null;
    }

    /// <summary>Return random ice-chars: letters and digits.</summary>
    private static string RandomIceChars(int length) => RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", length);
}
