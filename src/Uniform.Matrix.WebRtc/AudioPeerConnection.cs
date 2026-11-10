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
    /// <summary>Whether to accept an offer's VP8 video, for receiving; its frames come in <see cref="AudioPeerConnection.VideoReceived"/>.</summary>
    public bool ReceiveVideo { get; init; }
    /// <summary>Whether to send VP8 video when the peer's offer receives it, with <see cref="AudioPeerConnection.SendVideoAsync"/>.</summary>
    public bool SendVideo { get; init; }
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

/// <summary>A WebRTC peer connection with one Opus audio stream each way, as 1:1 voice calls use: SDP offer and answer, ICE with trickled candidates, DTLS-SRTP, and RTP; and, when the peer offers it and <see cref="PeerConnectionOptions.ReceiveVideo"/> is set, VP8 video received.</summary>
/// <remarks>
/// Received video comes as whole frames in <see cref="VideoReceived"/>; the connection sends RTCP receiver reports every second, and picture loss indications while a keyframe is needed.
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
    private IceAgent? restarting;
    private SessionDescription? lastLocal;
    private string? remoteUfrag;
    private string? restartRemoteUfrag;
    private readonly uint videoSsrc = RandomUInt();
    private readonly Lock videoLock = new();
    private byte? videoPayloadType;
    private Vp8Depacketizer? depacketizer;
    private ReceiverStatistics? videoStatistics;
    private long lastPictureLoss;
    private Task? reporting;
    private byte? videoSendPayloadType;
    private readonly Vp8Packetizer packetizer = new();
    private ushort videoSequence = (ushort)RandomUInt();
    private readonly uint videoTimestampBase = RandomUInt();
    private readonly long videoClockStart = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Create a connection; nothing happens until an offer is made or accepted.</summary>
    public AudioPeerConnection(PeerConnectionOptions? options = null) => this.options = options ?? new PeerConnectionOptions();

    /// <summary>The state.</summary>
    public PeerConnectionState State { get; private set; }

    /// <summary>The ICE agent, once an offer is made or accepted; e.g. to see the selected candidates.</summary>
    public IceAgent? Ice => ice;

    /// <summary>Whether an ICE restart is under way, between its offer and its connecting.</summary>
    public bool IsRestarting => restarting is not null;

    /// <summary>Raised for each local candidate, to trickle to the peer.</summary>
    public event Action<IceCandidate>? CandidateGathered;

    /// <summary>Raised when all candidates are gathered, e.g. to tell the peer that no more come.</summary>
    public event Action? GatheringCompleted;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<PeerConnectionState>? StateChanged;

    /// <summary>Raised for each audio packet received, decrypted, on the socket's read loop; its payload is Opus.</summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>Raised for each whole VP8 frame received, in order, on the socket's read loop; after lost packets, from the next keyframe.</summary>
    public event Action<Vp8EncodedFrame>? VideoReceived;

    /// <summary>Whether the answer accepted the peer's video, so frames may come in <see cref="VideoReceived"/>.</summary>
    public bool ReceivesVideo => videoPayloadType is not null;

    /// <summary>Whether the answer sends video to the peer, with <see cref="SendVideoAsync"/>.</summary>
    public bool SendsVideo => videoSendPayloadType is not null;

    /// <summary>Raised when the peer asks for a keyframe of the video sent, e.g. after it lost packets or joined; the encoder should make the next frame one. On the socket's read loop.</summary>
    public event Action? KeyframeRequested;

    /// <summary>Return an offer of audio, and with <paramref name="video"/> of video both ways, and start gathering; this side controls ICE. The answer decides which ways the video goes.</summary>
    public string CreateOffer(bool video = false)
    {
        // Agent and offer
        IceAgent agent = Start(controlling: true);
        lastLocal = AudioSdp.CreateOffer(Local(agent), video);
        // Return the offer
        return lastLocal.ToString();
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
        SetRemoteVideo(sdp);
        lastLocal = AudioSdp.CreateAnswer(sdp, Local(agent), receiveVideo: options.ReceiveVideo, sendVideo: options.SendVideo);
        // Return the answer
        return lastLocal.ToString();
    }

    /// <summary>Accept the peer's answer to this side's offer.</summary>
    /// <exception cref="InvalidOperationException">When no offer was made, or the answer has no Opus audio.</exception>
    public void AcceptAnswer(string answer)
    {
        // The peer's audio; its DTLS role decides this side's
        if (ice is null) throw new InvalidOperationException("No offer was made.");
        RemoteAudio audio = AudioSdp.Read(SessionDescription.Parse(answer)) ?? throw new InvalidOperationException("The answer has no Opus audio over DTLS-SRTP.");
        dtlsClient = audio.Setup == "passive";
        SetRemote(audio);
        // Done: the video the answer accepted, when offered
        if (lastLocal?.Media.Any(m => m.Kind == "video") == true) SetRemoteVideo(SessionDescription.Parse(answer));
    }

    /// <summary>Add a candidate the peer trickled, as its <c>candidate:</c> attribute, to the restarting agent while ICE restarts; unusable ones are ignored.</summary>
    public void AddRemoteCandidate(string candidate)
    {
        // Parsed, then paired
        if ((restarting ?? ice) is not IceAgent agent || IceCandidate.Parse(candidate) is not IceCandidate parsed) return;
        // Done
        agent.AddRemoteCandidate(parsed);
    }

    /// <summary>Start an <see href="https://www.rfc-editor.org/rfc/rfc8445#section-9">ICE restart</see>, e.g. after the network changed: a new agent with new credentials gathers on the networks there are now, while DTLS and SRTP go on; return the offer for the peer. This side controls the new agent.</summary>
    /// <exception cref="InvalidOperationException">Before the first offer and answer.</exception>
    public string CreateRestartOffer()
    {
        // Assert a negotiated connection; a restart under way gives way
        if (ice is null || dtls is null || lastLocal is null) throw new InvalidOperationException("Offer and answer first.");
        CancelRestart();
        IceAgent agent = NewAgent(controlling: true);
        restarting = agent;
        lastLocal = AudioSdp.CreateRestartOffer(lastLocal, Local(agent));
        // Return offer
        return lastLocal.ToString();
    }

    /// <summary>Answer the peer's new offer: an ICE restart when its credentials changed, with a new agent this side is controlled in, else the same connection; this side keeps its DTLS role either way.</summary>
    /// <exception cref="InvalidOperationException">Before the first offer and answer, or when the offer has no Opus audio.</exception>
    public string AcceptRestartOffer(string offer)
    {
        // The peer's audio
        if (ice is null || dtls is null) throw new InvalidOperationException("Offer and answer first.");
        SessionDescription sdp = SessionDescription.Parse(offer);
        RemoteAudio audio = AudioSdp.Read(sdp) ?? throw new InvalidOperationException("The offer has no Opus audio over DTLS-SRTP.");
        string setup = dtlsClient ? "active" : "passive";

        // Same credentials: nothing to restart
        if (audio.Ufrag == remoteUfrag)
        {
            lastLocal = AudioSdp.CreateAnswer(sdp, Local(ice), setup, options.ReceiveVideo, options.SendVideo);
            return lastLocal.ToString();
        }

        // New credentials: a new agent, this side's own restart giving way
        CancelRestart();
        IceAgent agent = NewAgent(controlling: false);
        restarting = agent;
        restartRemoteUfrag = audio.Ufrag;
        agent.SetRemoteCredentials(audio.Ufrag, audio.Password);
        foreach (IceCandidate candidate in audio.Candidates) agent.AddRemoteCandidate(candidate);
        lastLocal = AudioSdp.CreateAnswer(sdp, Local(agent), setup, options.ReceiveVideo, options.SendVideo);
        // Return answer
        return lastLocal.ToString();
    }

    /// <summary>Take the peer's answer to this side's restart offer.</summary>
    /// <exception cref="InvalidOperationException">When the answer has no Opus audio.</exception>
    public void AcceptRestartAnswer(string answer)
    {
        // The new agent's peer credentials
        if (restarting is not IceAgent agent) return;
        RemoteAudio audio = AudioSdp.Read(SessionDescription.Parse(answer)) ?? throw new InvalidOperationException("The answer has no Opus audio over DTLS-SRTP.");
        restartRemoteUfrag = audio.Ufrag;
        agent.SetRemoteCredentials(audio.Ufrag, audio.Password);
        // Done
        foreach (IceCandidate candidate in audio.Candidates) agent.AddRemoteCandidate(candidate);
    }

    /// <summary>Connect the restarting agent, and move the media to it; return whether it connected. The old agent goes on until then.</summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        // The agent of this restart
        if (restarting is not IceAgent agent) return false;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        bool connected = await agent.ConnectAsync(options.ConnectTimeout, linked.Token);
        if (restarting != agent) return false;
        if (!connected)
        {
            CancelRestart();
            return false;
        }

        // Switch, then close the old one
        IceAgent old = ice!;
        ice = agent;
        restarting = null;
        remoteUfrag = restartRemoteUfrag;
        SetState(PeerConnectionState.Connected);
        await old.DisposeAsync();
        // Done
        return true;
    }

    /// <summary>Abandon an ICE restart under way.</summary>
    public void CancelRestart()
    {
        // Close its agent
        if (restarting is not IceAgent agent) return;
        restarting = null;
        // Done
        _ = agent.DisposeAsync().AsTask();
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
        // Done, with reports on received video
        SetState(PeerConnectionState.Connected);
        if (videoPayloadType is not null) reporting ??= ReportAsync(stopping.Token);
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

    /// <summary>Send a VP8 frame of this side's video, captured now, in RTP packets of at most 1100 bytes, so they fit a path's MTU with SRTP and TURN; dropped until connected or when the peer receives no video.</summary>
    /// <param name="frame">The encoded frame, as the encoder wrote it.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async ValueTask SendVideoAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        // Connected, and sending
        if (outbound is null || ice is null || State != PeerConnectionState.Connected || videoSendPayloadType is not byte type) return;
        uint stamp = videoTimestampBase + (uint)(System.Diagnostics.Stopwatch.GetElapsedTime(videoClockStart).TotalSeconds * 90000);

        // Packets of one timestamp, the last marked
        IReadOnlyList<byte[]> payloads = packetizer.Packetize(frame, 1100);
        for (int i = 0; i < payloads.Count; i++)
        {
            RtpPacket packet = new() { PayloadType = type, Marker = i == payloads.Count - 1, Sequence = videoSequence++, Timestamp = stamp, Ssrc = videoSsrc, Payload = payloads[i] };
            await ice.SendAsync(outbound.ProtectRtp(packet.Encode()), cancellationToken);
        }
    }

    /// <summary>Ask the peer for a keyframe of its video with an RTCP picture loss indication, e.g. after a frame failed to decode; nothing without received video.</summary>
    public Task RequestKeyframeAsync()
    {
        // Received video only
        if (videoPayloadType is null) return Task.CompletedTask;
        lock (videoLock) lastPictureLoss = Environment.TickCount64;
        // Done
        return SendReportAsync(pictureLoss: true);
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
        CancelRestart();
        if (ice is not null) await ice.DisposeAsync();
        if (gathering is not null) await gathering.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (reporting is not null) await reporting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Done
        SetState(PeerConnectionState.Closed);
    }

    /// <summary>Create the first ICE agent, and start gathering.</summary>
    private IceAgent Start(bool controlling)
    {
        // Once
        if (ice is not null) throw new InvalidOperationException("An offer was already made or accepted.");
        ice = NewAgent(controlling);
        // Return agent
        return ice;
    }

    /// <summary>Create an ICE agent whose candidates and data go to this connection, and whose state counts while it is the current one; start gathering.</summary>
    private IceAgent NewAgent(bool controlling)
    {
        // Agent and its events
        IceAgent agent = new(new IceOptions { Controlling = controlling, Servers = options.Servers, IncludeLoopback = options.IncludeLoopback, RelayOnly = options.RelayOnly });
        agent.CandidateGathered += candidate => CandidateGathered?.Invoke(candidate);
        agent.DataReceived += OnData;
        agent.StateChanged += state => { if (agent == ice) OnIceState(state); };
        Task previous = gathering ?? Task.CompletedTask;
        gathering = Task.WhenAll(previous, GatherAsync(agent));
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
        remoteUfrag = audio.Ufrag;
        ice!.SetRemoteCredentials(audio.Ufrag, audio.Password);
        foreach (IceCandidate candidate in audio.Candidates) ice.AddRemoteCandidate(candidate);
        // Done; DTLS goes over whichever agent is current
        dtls = new DtlsSrtp(certificate, dtlsClient, audio.Fingerprint, datagram => ice!.SendAsync(datagram, stopping.Token).AsTask().Wait());
        dtls.Closed += () => SetState(PeerConnectionState.Closed);
    }

    /// <summary>Take the peer's video: when receiving is wanted and the peer sends, its payload type, a depacketizer and statistics; when sending is wanted and the peer receives, the payload type to send with.</summary>
    private void SetRemoteVideo(SessionDescription offer)
    {
        // Each way as the answer says
        if (AudioSdp.ReadVideo(offer) is not RemoteVideo video) return;
        string direction = AudioSdp.VideoDirection(video.Direction, options.ReceiveVideo, options.SendVideo);
        if (direction is "sendrecv" or "sendonly") videoSendPayloadType = video.Vp8PayloadType;
        if (direction is not ("sendrecv" or "recvonly")) return;
        videoPayloadType = video.Vp8PayloadType;
        depacketizer = new Vp8Depacketizer();
        // Done
        videoStatistics = new ReceiverStatistics(90000);
    }

    /// <summary>Count a video packet, give the frames it completes, and ask for a keyframe when one is needed, at most every 300 ms.</summary>
    private void OnVideo(RtpPacket packet)
    {
        // Frames in order
        IReadOnlyList<Vp8EncodedFrame> frames;
        bool askKeyframe;
        lock (videoLock)
        {
            videoStatistics!.Received(packet);
            frames = depacketizer!.Push(packet);
            askKeyframe = depacketizer.NeedsKeyframe && Environment.TickCount64 - lastPictureLoss > 300;
            if (askKeyframe) lastPictureLoss = Environment.TickCount64;
        }
        foreach (Vp8EncodedFrame frame in frames) VideoReceived?.Invoke(frame);
        // Done
        if (askKeyframe) _ = SendReportAsync(pictureLoss: true);
    }

    /// <summary>Send a receiver report every second while connected, with a picture loss indication while a keyframe is needed.</summary>
    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        // Until closed
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            bool needsKeyframe;
            lock (videoLock) needsKeyframe = depacketizer!.NeedsKeyframe;
            await SendReportAsync(needsKeyframe);
        }
    }

    /// <summary>Send a compound RTCP packet: the report on the received video, the CNAME, and with <paramref name="pictureLoss"/> a picture loss indication.</summary>
    private async Task SendReportAsync(bool pictureLoss)
    {
        // Nothing to report on before the first packet
        if (outbound is null || ice is null || State != PeerConnectionState.Connected) return;
        byte[] compound;
        lock (videoLock)
        {
            if (videoStatistics!.Report() is not ReportBlock block) return;
            compound = Rtcp.Compound(videoSsrc, [block], cname, pictureLoss ? block.Ssrc : null);
        }
        try
        {
            await ice.SendAsync(outbound.ProtectRtcp(compound), stopping.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // On a connection closing meanwhile
        }
    }

    /// <summary>Return this side's audio for the SDP.</summary>
    private LocalAudio Local(IceAgent agent) => new(agent.LocalUfrag, agent.LocalPassword, certificate.Fingerprint, ssrc, cname, videoSsrc);

    /// <summary>Route a datagram from the peer: DTLS to the handshake, SRTP audio to <see cref="AudioReceived"/> and video to <see cref="VideoReceived"/>, SRTCP checked and its sender reports noted.</summary>
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
            // Keyframe requests for the video sent; sender reports of the video received, for the round trip in the next report
            if (inbound.UnprotectRtcp(datagram) is not byte[] rtcp) return;
            if (videoSendPayloadType is not null && Rtcp.AsksKeyframe(rtcp, videoSsrc)) KeyframeRequested?.Invoke();
            if (videoStatistics is null) return;
            lock (videoLock)
            {
                foreach ((uint sender, uint ntp) in Rtcp.ReadSenderReports(rtcp))
                {
                    if (sender == videoStatistics.Ssrc) videoStatistics.SenderReport(ntp);
                }
            }
            return;
        }
        if (inbound.UnprotectRtp(datagram) is not byte[] plain || RtpPacket.Parse(plain) is not RtpPacket packet) return;
        if (packet.PayloadType == videoPayloadType)
        {
            OnVideo(packet);
            return;
        }
        if (packet.PayloadType != payloadType) return;
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
