namespace Uniform.Matrix.Calls;

using Microsoft.Extensions.Logging;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Video;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Ice;

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

/// <summary>A 1:1 call with another user, as the apps show it: its state, mute and camera, and the other's pictures; a <see cref="LegacyCall"/> of <c>m.call.*</c> events, or a <see cref="RtcCall"/> of MatrixRTC through a LiveKit SFU.</summary>
/// <remarks>The voice and video go over an <see cref="IMediaConnection"/>: <see cref="VoiceMedia"/> records and plays, a <see cref="VideoReceiver"/> decodes, and the camera's frames are sent. A call's actions and events run one at a time under <see cref="gate"/>.</remarks>
public abstract class VoiceCall : IAsyncDisposable
{
    private readonly Func<IAudioDevices?> devices;
    private readonly Func<IVideoCamera?> camera;
    private readonly Func<IVideoDecoder?> videoDecoder;
    /// <summary>Logger.</summary>
    private protected readonly ILogger logger;
    /// <summary>Runs the call's actions and events one at a time.</summary>
    private protected readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Cancelled when the call ends.</summary>
    private protected readonly CancellationTokenSource ending = new();
    private IMediaConnection? mediaConnection;
    private VoiceMedia? media;
    private VideoReceiver? video;
    private IVideoCamera? sending;
    private volatile bool cameraOff;
    private bool muted;

    /// <summary>Create a call; <see cref="CallService"/> does.</summary>
    /// <param name="roomId">Room of the call.</param>
    /// <param name="callId">The call's ID.</param>
    /// <param name="peerUserId">The other user's ID.</param>
    /// <param name="devices">Opens the microphone and speaker when connected; null for none, e.g. in tests.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="camera">Opens the camera when a call with video connects; null, or returning null, for none.</param>
    /// <param name="videoDecoder">Makes the decoder of the peer's video, e.g. the platform's hardware one; null, or returning null, for the C# one.</param>
    private protected VoiceCall(string roomId, string callId, string peerUserId, Func<IAudioDevices?> devices, ILogger logger, Func<IVideoCamera?>? camera, Func<IVideoDecoder?>? videoDecoder)
    {
        // Identity, and the parts the call uses
        RoomId = roomId;
        CallId = callId;
        PeerUserId = peerUserId;
        this.devices = devices;
        this.logger = logger;
        this.camera = camera ?? (() => null);
        this.videoDecoder = videoDecoder ?? (() => null);
    }

    /// <summary>Room of the call.</summary>
    public string RoomId { get; }

    /// <summary>The call's ID.</summary>
    public string CallId { get; }

    /// <summary>The other user.</summary>
    public string PeerUserId { get; }

    /// <summary>Whether the other user called.</summary>
    public abstract bool IsIncoming { get; }

    /// <summary>Whether the call has video, which this side can show.</summary>
    public bool IsVideo { get; private protected set; }

    /// <summary>Whether this side sends its camera's video in the call.</summary>
    public bool SendsVideo => sending is not null;

    /// <summary>Whether this side's device has another camera to switch to.</summary>
    public bool CanSwitchCamera => sending?.CanSwitch == true;

    /// <summary>Switch the camera sent, e.g. from the front to the back.</summary>
    public void SwitchCamera() => sending?.SwitchCamera();

    /// <summary>Whether this side's camera is turned off: nothing is captured or sent until it is turned on.</summary>
    public bool CameraOff
    {
        get => cameraOff;
        set
        {
            // The camera stops or starts; frames are dropped meanwhile
            cameraOff = value;
            sending?.SetPaused(value);
            OnCameraOff(value);
            Changed?.Invoke(this);
        }
    }

    /// <summary>Raised with small pictures of this side's camera, for a preview; on any thread.</summary>
    public event Action<VoiceCall, VideoFrame>? PreviewReceived;

    /// <summary>Raised with each picture of the peer's video, on a decoding thread; a handler should be quick, e.g. keep the newest picture to show.</summary>
    public event Action<VoiceCall, VideoFrame>? PictureReceived;

    /// <summary>The state.</summary>
    public CallState State { get; private set; }

    /// <summary>Why the call ended, e.g. <c>user_hangup</c>, <c>rejected</c>, <c>invite_timeout</c>, <c>ice_failed</c> or <c>answered_elsewhere</c>; null until ended.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Whether the other side ended the call.</summary>
    public bool EndedByPeer { get; private set; }

    /// <summary>When voice started to flow; null before.</summary>
    public DateTimeOffset? ConnectedAt { get; private protected set; }

    /// <summary>The local and remote candidates the media goes through, once connected, e.g. to show whether it is relayed.</summary>
    public virtual (IceCandidate? Local, IceCandidate? Remote) Path => (null, null);

    /// <summary>Whether this side's microphone is muted.</summary>
    public bool Muted
    {
        get => muted;
        set
        {
            // Now, and for media started later
            muted = value;
            if (media is not null) media.Muted = value;
            OnMuted(value);
            Changed?.Invoke(this);
        }
    }

    /// <summary>Raised when <see cref="State"/> or <see cref="Muted"/> changes; on any thread.</summary>
    public event Action<VoiceCall>? Changed;

    /// <summary>Answer an incoming call, and connect; this side sends no video.</summary>
    public Task AnswerAsync(CancellationToken cancellationToken = default) => AnswerAsync(video: false, cancellationToken);

    /// <summary>Answer an incoming call with this side's camera too, when the platform has a camera; and connect.</summary>
    public Task AnswerWithVideoAsync(CancellationToken cancellationToken = default) => AnswerAsync(video: true, cancellationToken);

    /// <summary>Answer, with or without video, and connect.</summary>
    private protected abstract Task AnswerAsync(bool video, CancellationToken cancellationToken);

    /// <summary>Decline an incoming call.</summary>
    public abstract Task RejectAsync(CancellationToken cancellationToken = default);

    /// <summary>Hang up, or cancel an outgoing call that rings.</summary>
    public abstract Task HangupAsync(string reason = "user_hangup", CancellationToken cancellationToken = default);

    /// <summary>Restart the media path, e.g. after the network changed; nothing where the call cannot.</summary>
    public virtual Task RestartIceAsync(string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>End the call without telling the peer, and release the media.</summary>
    public async ValueTask DisposeAsync()
    {
        // Done
        await EndAsync("disposed", byPeer: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Reopen the microphone and speaker, e.g. to switch between the earpiece and the loudspeaker, which phones choose when the speaker opens.</summary>
    public async Task RestartAudioAsync()
    {
        // While voice flows
        await gate.WaitAsync();
        try
        {
            if (State is not (CallState.Connected or CallState.Reconnecting) || mediaConnection is null) return;
            if (media is not null) await media.DisposeAsync();
            media = null;
            StartMedia(mediaConnection);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Tell the peer the microphone is muted or not, where the protocol does; nothing by default.</summary>
    private protected virtual void OnMuted(bool value) { }

    /// <summary>Tell the peer the camera is off or not, where the protocol does; nothing by default.</summary>
    private protected virtual void OnCameraOff(bool value) { }

    /// <summary>Close the call's connections after the media stopped, and tell the peer where the protocol does on ending; nothing by default.</summary>
    private protected virtual ValueTask OnEndedAsync() => ValueTask.CompletedTask;

    /// <summary>Open the devices and start the voice, unless there are none; a device that fails leaves the call without voice, not ended.</summary>
    private protected void StartMedia(IMediaConnection connection)
    {
        // Devices as chosen now
        mediaConnection = connection;
        if (devices() is not IAudioDevices audio) return;
        try
        {
            media = new VoiceMedia(connection, audio) { Muted = muted };
            media.DeviceFailed += e => logger.LogWarning(e, "Audio device of call {CallId} failed", CallId);
            media.Start();
        }
        catch (Exception e)
        {
            // On devices that did not open
            logger.LogWarning(e, "Audio devices of call {CallId} did not open", CallId);
        }
    }

    /// <summary>Open the camera and send its frames, when the connection sends video; a camera that fails leaves the call without it.</summary>
    private protected void StartCamera(IMediaConnection connection)
    {
        // Sending, with a camera
        if (!connection.SendsVideo || camera() is not IVideoCamera opened) return;
        try
        {
            opened.FrameEncoded += frame => { if (!cameraOff) _ = connection.SendVideoAsync(frame, ending.Token, opened.Rotation); };
            opened.Previewed += picture => { if (!cameraOff) PreviewReceived?.Invoke(this, picture); };
            connection.KeyframeRequested += opened.RequestKeyframe;
            connection.BitrateRequested += opened.LimitBitrate;
            opened.Upright = !connection.SendsOrientation;
            opened.Start();
            sending = opened;
        }
        catch (Exception e)
        {
            // On a camera that did not open
            logger.LogWarning(e, "Camera of call {CallId} did not open", CallId);
            _ = opened.DisposeAsync();
        }
    }

    /// <summary>Decode the peer's video from the start, so its first keyframe is not missed.</summary>
    private protected void StartVideo(IMediaConnection connection)
    {
        // Pictures to the call's listeners
        IVideoDecoder? platformDecoder = null;
        try { platformDecoder = videoDecoder(); }
        catch (Exception e) { logger.LogWarning(e, "The platform's video decoder did not start; decoding in C#"); }
        video = new VideoReceiver(connection, logger, platformDecoder);
        // Done
        video.PictureDecoded += picture => PictureReceived?.Invoke(this, picture);
    }

    /// <summary>End the call: stop the media, close the connections, and tell.</summary>
    private protected async Task EndAsync(string reason, bool byPeer)
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
        if (sending is not null) await sending.DisposeAsync();
        if (video is not null)
        {
            await video.DisposeAsync();
            logger.LogInformation("Call {CallId} video: {Decoded} pictures decoded in {Milliseconds:F1} ms on average, {Skipped} frames skipped", CallId, video.Decoded, video.AverageDecodeTime.TotalMilliseconds, video.Skipped);
        }
        try { await OnEndedAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Closing call {CallId} failed", CallId); }
        // Done
        logger.LogInformation("Call {CallId} in {RoomId} ended: {Reason}", CallId, RoomId, reason);
    }

    /// <summary>Set the state, and tell when it changed.</summary>
    private protected void SetState(CallState state)
    {
        // Changes only; ended stays ended
        if (State == state || (State == CallState.Ended && state != CallState.Ended)) return;
        State = state;
        // Done
        Changed?.Invoke(this);
    }
}
