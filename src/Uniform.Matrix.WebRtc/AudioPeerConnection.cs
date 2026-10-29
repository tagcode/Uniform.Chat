namespace Uniform.Matrix.WebRtc;

using System.Security.Cryptography;
using Uniform.Matrix.WebRtc.Dtls;
using Uniform.Matrix.WebRtc.Ice;
using Uniform.Matrix.WebRtc.Rtp;
using Uniform.Matrix.WebRtc.Sdp;
using Uniform.Matrix.WebRtc.Srtp;

/// <summary>Options of an <see cref="AudioPeerConnection"/>.</summary>
public sealed record PeerConnectionOptions
{
    /// <summary>STUN and TURN servers, e.g. from Matrix's <c>/voip/turnServer</c>.</summary>
    public IReadOnlyList<IceServer> Servers { get; init; } = [];
    /// <summary>Whether to use loopback addresses, e.g. for tests on one machine.</summary>
    public bool IncludeLoopback { get; init; }
    /// <summary>Whether to use relayed candidates only, so the peer does not learn this side's addresses.</summary>
    public bool RelayOnly { get; init; }
    /// <summary>How long ICE may take to connect.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The state of an <see cref="AudioPeerConnection"/>.</summary>
public enum PeerConnectionState
{
    /// <summary>Not yet connecting.</summary>
    New,
    /// <summary>ICE and DTLS are connecting.</summary>
    Connecting,
    /// <summary>Media flow.</summary>
    Connected,
    /// <summary>The peer stopped answering; it may come back.</summary>
    Disconnected,
    /// <summary>ICE or DTLS failed.</summary>
    Failed,
    /// <summary>Closed, by this side or the peer.</summary>
    Closed,
}

/// <summary>A WebRTC peer connection with one Opus audio stream each way, as 1:1 voice calls use: SDP offer and answer, ICE with trickled candidates, DTLS-SRTP, and RTP.</summary>
/// <remarks>
/// The caller calls <see cref="CreateOffer"/> and later <see cref="AcceptAnswer"/>; the callee calls <see cref="AcceptOffer"/>. Both trickle the candidates of <see cref="CandidateGathered"/> to the peer, give the peer's to <see cref="AddRemoteCandidate"/>, and call <see cref="ConnectAsync"/>.
/// The offerer controls ICE, and the answerer is the DTLS client, as browsers do. Datagrams are told apart by their first byte as <see href="https://www.rfc-editor.org/rfc/rfc7983">RFC 7983</see> says.
/// </remarks>
public sealed class AudioPeerConnection : IAsyncDisposable
{
    /// <summary>Samples per 20 ms frame at Opus's 48 kHz RTP clock.</summary>
    public const int FrameSamples = 960;
    private readonly PeerConnectionOptions options;
    private readonly DtlsCertificate certificate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly uint ssrc = RandomUInt();
    private readonly string cname = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
    private IceAgent? ice;
    private DtlsSrtp? dtls;
    private bool dtlsClient;
    private byte payloadType = AudioSdp.OpusPayloadType;
    private ushort sequence = (ushort)RandomUInt();
    private uint timestamp = RandomUInt();
    private bool silent = true;
    private SrtpSession? outbound;
    private SrtpSession? inbound;
    private Task? gathering;

    /// <summary>Create a connection; nothing happens until an offer is made or accepted.</summary>
    public AudioPeerConnection(PeerConnectionOptions? options = null) => this.options = options ?? new PeerConnectionOptions();

    /// <summary>The state.</summary>
    public PeerConnectionState State { get; private set; }

    /// <summary>The ICE agent, once an offer is made or accepted; e.g. to see the selected candidates.</summary>
    public IceAgent? Ice => ice;

    /// <summary>Raised for each local candidate, to trickle to the peer.</summary>
    public event Action<IceCandidate>? CandidateGathered;

    /// <summary>Raised when all candidates are gathered, e.g. to tell the peer that no more come.</summary>
    public event Action? GatheringCompleted;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<PeerConnectionState>? StateChanged;

    /// <summary>Raised for each audio packet received, decrypted, on the socket's read loop; its payload is Opus.</summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>Return an offer of audio, and start gathering; this side controls ICE.</summary>
    public string CreateOffer()
    {
        // Agent and offer
        IceAgent agent = Start(controlling: true);
        // Return the offer
        return AudioSdp.CreateOffer(Local(agent)).ToString();
    }

    /// <summary>Accept the peer's offer, and start gathering; return the answer, in which this side is the DTLS client.</summary>
    /// <exception cref="InvalidOperationException">When the offer has no Opus audio over DTLS-SRTP.</exception>
    public string AcceptOffer(string offer)
    {
        // The peer's audio, then the agent and answer
        SessionDescription sdp = SessionDescription.Parse(offer);
        RemoteAudio audio = AudioSdp.Read(sdp) ?? throw new InvalidOperationException("The offer has no Opus audio over DTLS-SRTP.");
        IceAgent agent = Start(controlling: false);
        dtlsClient = true;
        SetRemote(audio);
        // Return the answer
        return AudioSdp.CreateAnswer(sdp, Local(agent)).ToString();
    }

    /// <summary>Accept the peer's answer to this side's offer.</summary>
    /// <exception cref="InvalidOperationException">When no offer was made, or the answer has no Opus audio.</exception>
    public void AcceptAnswer(string answer)
    {
        // The peer's audio; its DTLS role decides this side's
        if (ice is null) throw new InvalidOperationException("No offer was made.");
        RemoteAudio audio = AudioSdp.Read(SessionDescription.Parse(answer)) ?? throw new InvalidOperationException("The answer has no Opus audio over DTLS-SRTP.");
        dtlsClient = audio.Setup == "passive";
        // Done
        SetRemote(audio);
    }

    /// <summary>Add a candidate the peer trickled, as its <c>candidate:</c> attribute; unusable ones are ignored.</summary>
    public void AddRemoteCandidate(string candidate)
    {
        // Parsed, then paired
        if (ice is null || IceCandidate.Parse(candidate) is not IceCandidate parsed) return;
        // Done
        ice.AddRemoteCandidate(parsed);
    }

    /// <summary>Connect ICE, then DTLS; return whether media can flow.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Assert an offer and answer
        if (ice is null || dtls is null) throw new InvalidOperationException("Offer and answer first.");
        SetState(PeerConnectionState.Connecting);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        if (!await ice.ConnectAsync(options.ConnectTimeout, linked.Token))
        {
            SetState(PeerConnectionState.Failed);
            return false;
        }

        // Keys from DTLS, over the selected pair
        try
        {
            (outbound, inbound) = await dtls.HandshakeAsync(linked.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // On a failed handshake
            SetState(PeerConnectionState.Failed);
            return false;
        }
        // Done
        SetState(PeerConnectionState.Connected);
        return true;
    }

    /// <summary>Send a frame of Opus, <paramref name="samples"/> long at 48 kHz; dropped until connected.</summary>
    /// <param name="opus">The encoded frame.</param>
    /// <param name="samples">Samples it covers, which advance the RTP timestamp.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public ValueTask SendAudioAsync(byte[] opus, int samples = FrameSamples, CancellationToken cancellationToken = default)
    {
        // Connected only
        if (outbound is null || ice is null || State != PeerConnectionState.Connected) return ValueTask.CompletedTask;
        RtpPacket packet = new() { PayloadType = payloadType, Marker = silent, Sequence = sequence++, Timestamp = timestamp, Ssrc = ssrc, Payload = opus };
        timestamp += (uint)samples;
        silent = false;
        // Done
        return ice.SendAsync(outbound.ProtectRtp(packet.Encode()), cancellationToken);
    }

    /// <summary>Skip <paramref name="samples"/> without sending, e.g. while muted, so the timestamps stay in time; the next packet is marked.</summary>
    public void SkipAudio(int samples = FrameSamples)
    {
        // Time goes on
        timestamp += (uint)samples;
        silent = true;
    }

    /// <summary>Close DTLS and ICE.</summary>
    public async ValueTask DisposeAsync()
    {
        // DTLS's close_notify first, while the path is up
        if (State == PeerConnectionState.Closed) return;
        await stopping.CancelAsync();
        dtls?.Dispose();
        if (ice is not null) await ice.DisposeAsync();
        if (gathering is not null) await gathering.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Done
        SetState(PeerConnectionState.Closed);
    }

    /// <summary>Create the ICE agent, and start gathering.</summary>
    private IceAgent Start(bool controlling)
    {
        // Once
        if (ice is not null) throw new InvalidOperationException("An offer was already made or accepted.");
        IceAgent agent = new(new IceOptions { Controlling = controlling, Servers = options.Servers, IncludeLoopback = options.IncludeLoopback, RelayOnly = options.RelayOnly });
        ice = agent;
        agent.CandidateGathered += candidate => CandidateGathered?.Invoke(candidate);
        agent.DataReceived += OnData;
        agent.StateChanged += OnIceState;
        gathering = GatherAsync(agent);
        // Return agent
        return agent;
    }

    /// <summary>Gather, then tell it is done.</summary>
    private async Task GatherAsync(IceAgent agent)
    {
        // A yield first, so that the caller has the offer or answer before the candidates
        await Task.Yield();
        await agent.GatherAsync(stopping.Token);
        // Done
        GatheringCompleted?.Invoke();
    }

    /// <summary>Take the peer's audio: its ICE credentials and candidates, its payload type, and DTLS with its fingerprint.</summary>
    private void SetRemote(RemoteAudio audio)
    {
        // ICE, then DTLS, which takes records from now on
        payloadType = audio.OpusPayloadType;
        ice!.SetRemoteCredentials(audio.Ufrag, audio.Password);
        foreach (IceCandidate candidate in audio.Candidates) ice.AddRemoteCandidate(candidate);
        IceAgent agent = ice;
        // Done
        dtls = new DtlsSrtp(certificate, dtlsClient, audio.Fingerprint, datagram => agent.SendAsync(datagram, stopping.Token).AsTask().Wait());
        dtls.Closed += () => SetState(PeerConnectionState.Closed);
    }

    /// <summary>Return this side's audio for the SDP.</summary>
    private LocalAudio Local(IceAgent agent) => new(agent.LocalUfrag, agent.LocalPassword, certificate.Fingerprint, ssrc, cname);

    /// <summary>Route a datagram from the peer: DTLS to the handshake, SRTP to <see cref="AudioReceived"/>, SRTCP checked and dropped.</summary>
    private void OnData(byte[] datagram)
    {
        // DTLS: first byte 20 to 63
        if (datagram.Length > 0 && datagram[0] is >= 20 and <= 63)
        {
            dtls?.Receive(datagram);
            return;
        }

        // SRTP and SRTCP once keyed
        if (inbound is null || !RtpPacket.IsRtpOrRtcp(datagram)) return;
        if (RtpPacket.IsRtcp(datagram))
        {
            inbound.UnprotectRtcp(datagram);
            return;
        }
        if (inbound.UnprotectRtp(datagram) is not byte[] plain || RtpPacket.Parse(plain) is not RtpPacket packet || packet.PayloadType != payloadType) return;
        // Done
        AudioReceived?.Invoke(packet);
    }

    /// <summary>Follow ICE's connectivity once connected.</summary>
    private void OnIceState(IceState state)
    {
        // Disconnects and returns
        if (State is not (PeerConnectionState.Connected or PeerConnectionState.Disconnected)) return;
        if (state == IceState.Disconnected) SetState(PeerConnectionState.Disconnected);
        if (state == IceState.Connected) SetState(PeerConnectionState.Connected);
    }

    /// <summary>Set the state, and tell when it changed.</summary>
    private void SetState(PeerConnectionState state)
    {
        // Changes only; closed stays closed
        if (State == state || State == PeerConnectionState.Closed) return;
        State = state;
        // Done
        StateChanged?.Invoke(state);
    }

    /// <summary>Return a random 32-bit number.</summary>
    private static uint RandomUInt() => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
}
