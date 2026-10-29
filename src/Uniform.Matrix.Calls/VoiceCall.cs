namespace Uniform.Matrix.Calls;

using Microsoft.Extensions.Logging;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>Sends a call event into the call's room.</summary>
public delegate Task CallEventSender(string eventType, CallContent content, CancellationToken cancellationToken);

/// <summary>The state of a <see cref="VoiceCall"/>.</summary>
public enum CallState
{
    /// <summary>An incoming call, not yet answered.</summary>
    Ringing,
    /// <summary>An outgoing call, not yet answered.</summary>
    Inviting,
    /// <summary>Answered; the media path is connecting.</summary>
    Connecting,
    /// <summary>Voice flows.</summary>
    Connected,
    /// <summary>The network path broke; it may come back.</summary>
    Reconnecting,
    /// <summary>Ended; <see cref="VoiceCall.EndReason"/> says why.</summary>
    Ended,
}

/// <summary>One <see href="https://spec.matrix.org/latest/client-server-api/#voice-over-ip">Matrix 1:1 voice call</see>, version 1: the invite, answer and select_answer, trickled candidates, and the hangup or reject; with the media of an <see cref="AudioPeerConnection"/> and <see cref="VoiceMedia"/>.</summary>
/// <remarks>Events of the call reach <see cref="HandleAsync"/>, which runs them one at a time with this side's actions. Candidates are sent in batches, as Element does, and end with an empty one; those received before the offer or answer wait for it.</remarks>
public sealed class VoiceCall : IAsyncDisposable
{
    /// <summary>How long an invite rings.</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(60);
    /// <summary>How long gathered candidates wait to be sent together.</summary>
    private static readonly TimeSpan CandidateBatchDelay = TimeSpan.FromMilliseconds(100);
    /// <summary>How long to wait for the peer's hangup after it closed the connection.</summary>
    private static readonly TimeSpan PeerHangupWait = TimeSpan.FromSeconds(5);
    private readonly string partyId;
    private readonly string ownUserId;
    private readonly CallEventSender send;
    private readonly Func<IAudioDevices?> devices;
    private readonly PeerConnectionOptions options;
    private readonly ILogger logger;
    private readonly CallInviteContent? invite;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource ending = new();
    private readonly List<CallCandidate> outgoingCandidates = [];
    private readonly Dictionary<string, List<string>> incomingCandidates = [];
    private readonly Lock candidateLock = new();
    private AudioPeerConnection? connection;
    private VoiceMedia? media;
    private string? peerPartyId;
    private bool remoteDescribed;
    private bool flushScheduled;
    private bool muted;

    /// <summary>Create a call; <see cref="CallService"/> does.</summary>
    /// <param name="roomId">Room of the call.</param>
    /// <param name="callId">The call's ID.</param>
    /// <param name="partyId">This client's party ID.</param>
    /// <param name="ownUserId">This user's ID.</param>
    /// <param name="peerUserId">The other user's ID.</param>
    /// <param name="invite">The invite of an incoming call; null for an outgoing one.</param>
    /// <param name="send">Sends the call's events.</param>
    /// <param name="devices">Opens the microphone and speaker when connected; null for none, e.g. in tests.</param>
    /// <param name="options">Options of the peer connection, with the TURN servers.</param>
    /// <param name="logger">Logger.</param>
    internal VoiceCall(string roomId, string callId, string partyId, string ownUserId, string peerUserId, CallInviteContent? invite, CallEventSender send, Func<IAudioDevices?> devices, PeerConnectionOptions options, ILogger logger)
    {
        // Identity, and the parts the call uses
        RoomId = roomId;
        CallId = callId;
        this.partyId = partyId;
        this.ownUserId = ownUserId;
        PeerUserId = peerUserId;
        this.invite = invite;
        peerPartyId = invite?.PartyId;
        this.send = send;
        this.devices = devices;
        this.options = options;
        this.logger = logger;
        State = invite is null ? CallState.Inviting : CallState.Ringing;
    }

    /// <summary>Room of the call.</summary>
    public string RoomId { get; }

    /// <summary>The call's ID.</summary>
    public string CallId { get; }

    /// <summary>The other user.</summary>
    public string PeerUserId { get; }

    /// <summary>Whether the other user called.</summary>
    public bool IsIncoming => invite is not null;

    /// <summary>The state.</summary>
    public CallState State { get; private set; }

    /// <summary>Why the call ended, e.g. <c>user_hangup</c>, <c>rejected</c>, <c>invite_timeout</c>, <c>ice_failed</c> or <c>answered_elsewhere</c>; null until ended.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Whether the other side ended the call.</summary>
    public bool EndedByPeer { get; private set; }

    /// <summary>When voice started to flow; null before.</summary>
    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>The local and remote candidates the media goes through, once connected, e.g. to show whether it is relayed.</summary>
    public (IceCandidate? Local, IceCandidate? Remote) Path => (connection?.Ice?.SelectedLocal, connection?.Ice?.SelectedRemote);

    /// <summary>Whether this side's microphone is muted.</summary>
    public bool Muted
    {
        get => muted;
        set
        {
            // Now, and for media started later
            muted = value;
            if (media is not null) media.Muted = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>Raised when <see cref="State"/> or <see cref="Muted"/> changes; on any thread.</summary>
    public event Action<VoiceCall>? Changed;

    /// <summary>Send the invite with an offer, and ring until answered or <see cref="InviteLifetime"/> passes.</summary>
    internal async Task InviteAsync(CancellationToken cancellationToken)
    {
        // Offer, then the invite before any candidate
        await gate.WaitAsync(cancellationToken);
        try
        {
            AudioPeerConnection peer = Connect();
            string offer = peer.CreateOffer();
            await send(EventTypes.CallInvite, new CallInviteContent { CallId = CallId, PartyId = partyId, Lifetime = (long)InviteLifetime.TotalMilliseconds, Invitee = PeerUserId, Offer = new CallSessionDescription { Type = "offer", Sdp = offer } }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
        // Done
        _ = TimeOutAsync(InviteLifetime);
    }

    /// <summary>Answer an incoming call, and connect.</summary>
    public async Task AnswerAsync(CancellationToken cancellationToken = default)
    {
        // Ringing only
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (State != CallState.Ringing || invite is null) return;
            AudioPeerConnection peer = Connect();
            string answer = peer.AcceptOffer(invite.Offer.Sdp);
            await send(EventTypes.CallAnswer, new CallAnswerContent { CallId = CallId, PartyId = partyId, Answer = new CallSessionDescription { Type = "answer", Sdp = answer } }, cancellationToken);
            SetRemoteDescribed();
            SetState(CallState.Connecting);
        }
        finally
        {
            gate.Release();
        }
        // Done
        _ = ConnectMediaAsync();
    }

    /// <summary>Decline an incoming call.</summary>
    public async Task RejectAsync(CancellationToken cancellationToken = default)
    {
        // Version 1 rejects; version 0 only knows hangup
        if (State != CallState.Ringing) return;
        if (invite?.Version == "0") await send(EventTypes.CallHangup, new CallHangupContent { CallId = CallId, Version = "0" }, cancellationToken);
        else await send(EventTypes.CallReject, new CallRejectContent { CallId = CallId, PartyId = partyId }, cancellationToken);
        // Done
        await EndAsync("rejected", byPeer: false);
    }

    /// <summary>Hang up, or cancel an outgoing call that rings.</summary>
    public async Task HangupAsync(string reason = "user_hangup", CancellationToken cancellationToken = default)
    {
        // Once
        if (State == CallState.Ended) return;
        try { await send(EventTypes.CallHangup, new CallHangupContent { CallId = CallId, PartyId = partyId, Reason = reason }, cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Hangup of call {CallId} not sent", CallId); }
        // Done
        await EndAsync(reason, byPeer: false);
    }

    /// <summary>Handle an event of this call from the room, from <paramref name="sender"/>; this side's own events are ignored.</summary>
    internal async Task HandleAsync(string sender, string eventType, CallContent content)
    {
        // Not our own echo, not after the end
        if (content.PartyId == partyId && sender == ownUserId) return;
        await gate.WaitAsync();
        try
        {
            if (State == CallState.Ended) return;
            switch (eventType)
            {
                case EventTypes.CallAnswer when content is CallAnswerContent answer:
                    await OnAnswerAsync(sender, answer);
                    break;
                case EventTypes.CallSelectAnswer when content is CallSelectAnswerContent select:
                    if (IsIncoming && sender == PeerUserId && select.SelectedPartyId != partyId) await EndAsync("answered_elsewhere", byPeer: false);
                    break;
                case EventTypes.CallCandidates when content is CallCandidatesContent candidates:
                    if (sender == PeerUserId) AddCandidates(content.PartyId ?? "", candidates.Candidates);
                    break;
                case EventTypes.CallReject:
                    if (sender == PeerUserId && !IsIncoming) await EndAsync("rejected", byPeer: true);
                    if (sender == ownUserId && IsIncoming) await EndAsync("rejected_elsewhere", byPeer: false);
                    break;
                case EventTypes.CallHangup when content is CallHangupContent hangup:
                    if (sender == PeerUserId && (peerPartyId is null || content.PartyId is null || content.PartyId == peerPartyId)) await EndAsync(hangup.Reason ?? "user_hangup", byPeer: true);
                    break;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>End the call without telling the peer, and release the media.</summary>
    public async ValueTask DisposeAsync() => await EndAsync("disposed", byPeer: false);

    /// <summary>Take the peer's answer to an outgoing call, or notice that another of this user's devices answered an incoming one.</summary>
    private async Task OnAnswerAsync(string sender, CallAnswerContent answer)
    {
        // Answered on another device of ours
        if (IsIncoming)
        {
            if (sender == ownUserId && State == CallState.Ringing) await EndAsync("answered_elsewhere", byPeer: false);
            return;
        }

        // The first answer of the peer wins
        if (sender != PeerUserId || State != CallState.Inviting || connection is null) return;
        peerPartyId = answer.PartyId;
        if (answer.PartyId is not null) await send(EventTypes.CallSelectAnswer, new CallSelectAnswerContent { CallId = CallId, PartyId = partyId, SelectedPartyId = answer.PartyId }, ending.Token);
        connection.AcceptAnswer(answer.Answer.Sdp);
        SetRemoteDescribed();
        SetState(CallState.Connecting);
        // Done
        _ = ConnectMediaAsync();
    }

    /// <summary>Create the peer connection, sending its candidates in batches.</summary>
    private AudioPeerConnection Connect()
    {
        // Candidates to the room, and the connection's fate to the call
        AudioPeerConnection peer = new(options);
        connection = peer;
        peer.CandidateGathered += candidate => QueueCandidate(new CallCandidate { Candidate = candidate.ToString(), SdpMid = "0", SdpMLineIndex = 0 });
        peer.GatheringCompleted += () => QueueCandidate(new CallCandidate { Candidate = "", SdpMid = "0", SdpMLineIndex = 0 });
        peer.StateChanged += OnConnectionState;
        // Return connection
        return peer;
    }

    /// <summary>Queue a candidate, and schedule sending the batch.</summary>
    private void QueueCandidate(CallCandidate candidate)
    {
        // One send per batch
        lock (candidateLock)
        {
            outgoingCandidates.Add(candidate);
            if (flushScheduled) return;
            flushScheduled = true;
        }
        // Done
        _ = FlushCandidatesAsync();
    }

    /// <summary>Send the queued candidates after a short wait for more.</summary>
    private async Task FlushCandidatesAsync()
    {
        // The batch so far
        await Task.Delay(CandidateBatchDelay);
        CallCandidate[] batch;
        lock (candidateLock)
        {
            batch = [.. outgoingCandidates];
            outgoingCandidates.Clear();
            flushScheduled = false;
        }
        if (batch.Length == 0 || State == CallState.Ended) return;
        // Done
        try { await send(EventTypes.CallCandidates, new CallCandidatesContent { CallId = CallId, PartyId = partyId, Candidates = batch }, ending.Token); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Candidates of call {CallId} not sent", CallId); }
    }

    /// <summary>Add the peer's candidates, or keep them until the offer or answer is applied.</summary>
    private void AddCandidates(string fromParty, IEnumerable<CallCandidate> candidates)
    {
        // Kept by party until the peer's description is known
        if (!remoteDescribed || connection is null)
        {
            if (!incomingCandidates.TryGetValue(fromParty, out List<string>? kept)) incomingCandidates[fromParty] = kept = [];
            kept.AddRange(candidates.Select(c => c.Candidate));
            return;
        }

        // Only the chosen party's
        if (peerPartyId is not null && fromParty.Length > 0 && fromParty != peerPartyId) return;
        // Done
        foreach (CallCandidate candidate in candidates.Where(c => c.Candidate.Length > 0)) connection.AddRemoteCandidate(candidate.Candidate);
    }

    /// <summary>Mark the peer's description applied, and add the candidates kept for its party.</summary>
    private void SetRemoteDescribed()
    {
        // Kept candidates of the chosen party, or of any when unknown
        remoteDescribed = true;
        foreach ((string party, List<string> kept) in incomingCandidates)
        {
            if (peerPartyId is not null && party.Length > 0 && party != peerPartyId) continue;
            foreach (string candidate in kept.Where(c => c.Length > 0)) connection!.AddRemoteCandidate(candidate);
        }
        // Done
        incomingCandidates.Clear();
    }

    /// <summary>Connect the peer connection, then start the voice; hang up when it fails.</summary>
    private async Task ConnectMediaAsync()
    {
        // ICE and DTLS
        AudioPeerConnection peer = connection!;
        bool connected;
        try { connected = await peer.ConnectAsync(ending.Token); }
        catch (OperationCanceledException) { return; }
        if (!connected)
        {
            await HangupAsync("ice_failed");
            return;
        }

        // Voice, unless the call ended meanwhile or there are no devices
        await gate.WaitAsync();
        try
        {
            if (State == CallState.Ended) return;
            if (devices() is IAudioDevices audio)
            {
                try
                {
                    media = new VoiceMedia(peer, audio) { Muted = muted };
                    media.Start();
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Audio devices of call {CallId} did not open", CallId);
                }
            }
            ConnectedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Call {CallId} connected from {Local} to {Remote}", CallId, peer.Ice?.SelectedLocal, peer.Ice?.SelectedRemote);
            SetState(CallState.Connected);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Follow the peer connection: reconnecting while ICE is down; ended when it fails, or when the peer closes it and its hangup does not follow.</summary>
    private void OnConnectionState(PeerConnectionState state)
    {
        // After connecting only
        if (State is not (CallState.Connected or CallState.Reconnecting)) return;
        if (state == PeerConnectionState.Disconnected) SetState(CallState.Reconnecting);
        else if (state == PeerConnectionState.Connected) SetState(CallState.Connected);
        else if (state == PeerConnectionState.Failed) _ = HangupAsync("ice_failed");
        // Done
        else if (state == PeerConnectionState.Closed) _ = AwaitHangupAsync();
    }

    /// <summary>Wait for the peer's hangup after it closed the connection, as Element closes before its hangup event arrives; hang up when none comes.</summary>
    private async Task AwaitHangupAsync()
    {
        // The hangup ends the call meanwhile
        try { await Task.Delay(PeerHangupWait, ending.Token); }
        catch (OperationCanceledException) { return; }
        // Done
        await HangupAsync("ice_failed");
    }

    /// <summary>Hang up an outgoing call, or stop ringing an incoming one, when nobody answered in time.</summary>
    private async Task TimeOutAsync(TimeSpan lifetime)
    {
        // Unless answered or ended
        try { await Task.Delay(lifetime, ending.Token); }
        catch (OperationCanceledException) { return; }
        if (State is CallState.Inviting) await HangupAsync("invite_timeout");
        // Done
        else if (State is CallState.Ringing) await EndAsync("invite_timeout", byPeer: true);
    }

    /// <summary>Start ringing an incoming call until its invite expires.</summary>
    internal void Ring(TimeSpan remaining) => _ = TimeOutAsync(remaining);

    /// <summary>End the call: stop the media, close the connection, and tell.</summary>
    private async Task EndAsync(string reason, bool byPeer)
    {
        // Once
        if (State == CallState.Ended) return;
        EndReason = reason;
        EndedByPeer = byPeer;
        SetState(CallState.Ended);
        await ending.CancelAsync();
        if (media is not null)
        {
            await media.DisposeAsync();
            logger.LogInformation("Call {CallId} voice: {Sent} frames sent, {Received} received, {Lost} lost, {Underruns} waited for", CallId, media.Sent, media.Received, media.Lost, media.Underruns);
        }
        if (connection is not null) await connection.DisposeAsync();
        // Done
        logger.LogInformation("Call {CallId} in {RoomId} ended: {Reason}", CallId, RoomId, reason);
    }

    /// <summary>Set the state, and tell when it changed.</summary>
    private void SetState(CallState state)
    {
        // Changes only; ended stays ended
        if (State == state || (State == CallState.Ended && state != CallState.Ended)) return;
        State = state;
        // Done
        Changed?.Invoke(this);
    }
}
