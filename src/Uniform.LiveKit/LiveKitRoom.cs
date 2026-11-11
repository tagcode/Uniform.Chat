namespace Uniform.LiveKit;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using global::LiveKit.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>Options of a <see cref="LiveKitRoom"/>.</summary>
public sealed record LiveKitRoomOptions
{
    /// <summary>STUN and TURN servers besides those LiveKit gives, e.g. the homeserver's.</summary>
    public IReadOnlyList<IceServer> Servers { get; init; } = [];
    /// <summary>Whether to use loopback addresses, e.g. for a LiveKit server on this machine.</summary>
    public bool IncludeLoopback { get; init; }
    /// <summary>Whether to use relayed candidates only.</summary>
    public bool RelayOnly { get; init; }
    /// <summary>Whether to receive the video of others.</summary>
    public bool ReceiveVideo { get; init; } = true;
    /// <summary>How long joining and publishing may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>A participant's connection to a LiveKit room: the signalling, the subscriber peer connection the server offers with the others' tracks, and the publisher peer connection this side offers with its own.</summary>
/// <remarks>The server subscribes this participant to every track, and offers each anew on the subscriber connection. <see cref="IdentityOf"/> tells whose a received stream is, by the <c>msid</c> of its section, <c>PA_…|TR_…</c>.</remarks>
public sealed class LiveKitRoom : IAsyncDisposable
{
    private readonly LiveKitSignalClient signal;
    private readonly LiveKitRoomOptions options;
    private readonly ILogger logger;
    private readonly IReadOnlyList<IceServer> servers;
    private readonly Channel<SignalResponse> inbox = Channel.CreateUnbounded<SignalResponse>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, ParticipantInfo> participants = new();
    private readonly ConcurrentDictionary<uint, string> streams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TrackInfo>> pendingTracks = new();
    private readonly List<string> earlySubscriberCandidates = [];
    private readonly Lock candidateLock = new();
    private readonly Task processing;
    private TaskCompletionSource<string>? publisherAnswer;
    private Task? subscriberConnecting;
    private Task? publisherConnecting;

    /// <summary>Create a room over a joined signalling client.</summary>
    private LiveKitRoom(LiveKitSignalClient signal, LiveKitRoomOptions options, ILogger logger)
    {
        // The join's participants and ICE servers, then the messages in order
        this.signal = signal;
        this.options = options;
        this.logger = logger;
        servers = [.. options.Servers, .. signal.Join.IceServers.SelectMany(s => s.Urls.Select(u => new IceServer(u, NullIfEmpty(s.Username), NullIfEmpty(s.Credential))))];
        foreach (ParticipantInfo participant in signal.Join.OtherParticipants) participants[participant.Sid] = participant;
        signal.MessageReceived += message => inbox.Writer.TryWrite(message);
        signal.Closed += () => { inbox.Writer.TryComplete(); Closed?.Invoke(); };
        processing = ProcessAsync();
        signal.Start();
    }

    /// <summary>This participant, as the server knows it.</summary>
    public ParticipantInfo LocalParticipant => signal.Join.Participant;

    /// <summary>The room's name.</summary>
    public string Name => signal.Join.Room.Name;

    /// <summary>The others in the room.</summary>
    public IReadOnlyCollection<ParticipantInfo> Participants => [.. participants.Values];

    /// <summary>The subscriber connection, once the server offered it.</summary>
    public AudioPeerConnection? Subscriber { get; private set; }

    /// <summary>The publisher connection, once this side published.</summary>
    public AudioPeerConnection? Publisher { get; private set; }

    /// <summary>The trailer of frames the server injects, which come unencrypted.</summary>
    public byte[] ServerTrailer => signal.Join.SifTrailer.ToByteArray();

    /// <summary>Raised when the subscriber connection is created, before it connects, e.g. to listen to its media.</summary>
    public event Action<AudioPeerConnection>? SubscriberCreated;

    /// <summary>Raised when another participant joins, changes or leaves; on the signalling loop.</summary>
    public event Action<ParticipantInfo>? ParticipantChanged;

    /// <summary>Raised when the server closed the signalling or asked this participant to leave.</summary>
    public event Action? Closed;

    /// <summary>Join the room at <paramref name="url"/> with <paramref name="token"/>.</summary>
    /// <exception cref="System.Net.WebSockets.WebSocketException">When the server cannot be reached or refuses the token.</exception>
    public static async Task<LiveKitRoom> ConnectAsync(string url, string token, LiveKitRoomOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        // Signalling, joined
        options ??= new LiveKitRoomOptions();
        logger ??= NullLogger.Instance;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        LiveKitSignalClient signal = await LiveKitSignalClient.ConnectAsync(url, token, logger, cancellationToken: timeout.Token);
        logger.LogInformation("Joined LiveKit room {Room} as {Identity} ({Sid}), with {Others} others, server {Version}", signal.Join.Room.Name, signal.Join.Participant.Identity, signal.Join.Participant.Sid, signal.Join.OtherParticipants.Count, signal.Join.ServerVersion);
        // Return room
        return new LiveKitRoom(signal, options, logger);
    }

    /// <summary>Return the identity of the participant whose stream <paramref name="ssrc"/> is, as the subscriber's offers said; null when unknown.</summary>
    public string? IdentityOf(uint ssrc)
    {
        // Stream, its participant
        if (!streams.TryGetValue(ssrc, out string? participantSid)) return null;
        // Return identity
        return participants.TryGetValue(participantSid, out ParticipantInfo? participant) ? participant.Identity : null;
    }

    /// <summary>Publish this side's microphone, and with <paramref name="video"/> its camera, encrypted as <paramref name="encryption"/> says; return once the publisher connection connected.</summary>
    /// <param name="video">Whether to publish video too.</param>
    /// <param name="encryption">How the frames are encrypted, which subscribers learn from the track's info.</param>
    /// <param name="width">Width of the video sent.</param>
    /// <param name="height">Height of the video sent.</param>
    /// <param name="cancellationToken">Cancels publishing.</param>
    /// <exception cref="TimeoutException">When the server does not take the tracks in time.</exception>
    public async Task<AudioPeerConnection> PublishAsync(bool video, Encryption.Types.Type encryption = Encryption.Types.Type.None, int width = 640, int height = 480, CancellationToken cancellationToken = default)
    {
        // Assert once; the tracks first, each awaited
        if (Publisher is not null) throw new InvalidOperationException("Already published.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(options.Timeout);
        string streamId = Guid.NewGuid().ToString();
        string audioCid = Guid.NewGuid().ToString(), videoCid = Guid.NewGuid().ToString();
        List<Task<TrackInfo>> published = [AddTrackAsync(new AddTrackRequest { Cid = audioCid, Name = "microphone", Type = TrackType.Audio, Source = TrackSource.Microphone, Encryption = encryption, Stream = "camera" })];
        if (video) published.Add(AddTrackAsync(new AddTrackRequest { Cid = videoCid, Name = "camera", Type = TrackType.Video, Source = TrackSource.Camera, Width = (uint)width, Height = (uint)height, Encryption = encryption, Stream = "camera", Layers = { new VideoLayer { Quality = VideoQuality.High, Width = (uint)width, Height = (uint)height } } }));
        TrackInfo[] tracks = await Task.WhenAll(published).WaitAsync(timeout.Token);
        AudioTrackSid = tracks[0].Sid;
        VideoTrackSid = video ? tracks[1].Sid : null;

        // The publisher's offer, its answer, then the connection
        AudioPeerConnection publisher = new(new PeerConnectionOptions { Servers = servers, IncludeLoopback = options.IncludeLoopback, RelayOnly = options.RelayOnly, SendOnly = true, SendVideo = video, StreamId = streamId, AudioTrackId = audioCid, VideoTrackId = videoCid });
        Publisher = publisher;
        publisher.CandidateGathered += candidate => _ = TrickleAsync(publisher, candidate, SignalTarget.Publisher);
        publisherAnswer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string offer = publisher.CreateOffer(video);
        await signal.SendAsync(new SignalRequest { Offer = new SessionDescription { Type = "offer", Sdp = offer, Id = 1 } }, timeout.Token);
        publisher.AcceptAnswer(await publisherAnswer.Task.WaitAsync(timeout.Token));
        publisherConnecting = publisher.ConnectAsync(stopping.Token);
        if (!await (Task<bool>)publisherConnecting) throw new TimeoutException("The publisher connection did not connect.");
        logger.LogInformation("Published to LiveKit room {Room}: audio {Audio}, video {Video}", Name, AudioTrackSid, VideoTrackSid);
        // Return publisher
        return publisher;
    }

    /// <summary>The server's ID of the published microphone, once published.</summary>
    public string? AudioTrackSid { get; private set; }

    /// <summary>The server's ID of the published camera, once published with video.</summary>
    public string? VideoTrackSid { get; private set; }

    /// <summary>Tell the others that a published track is muted or not, e.g. the camera turned off.</summary>
    public Task MuteAsync(bool video, bool muted, CancellationToken cancellationToken = default) => (video ? VideoTrackSid : AudioTrackSid) is string sid
        ? signal.SendAsync(new SignalRequest { Mute = new MuteTrackRequest { Sid = sid, Muted = muted } }, cancellationToken)
        : Task.CompletedTask;

    /// <summary>Ask the server for the quality of the others' video, e.g. a low one for a device that cannot decode more; the server picks the sender's layer of it.</summary>
    public Task SetVideoQualityAsync(VideoQuality quality, CancellationToken cancellationToken = default)
    {
        // Each video track of the others
        string[] sids = [.. participants.Values.SelectMany(p => p.Tracks).Where(t => t.Type == TrackType.Video).Select(t => t.Sid)];
        if (sids.Length == 0) return Task.CompletedTask;
        // Done
        return signal.SendAsync(new SignalRequest { TrackSetting = new UpdateTrackSettings { TrackSids = { sids }, Quality = quality } }, cancellationToken);
    }

    /// <summary>Leave the room, and close the connections.</summary>
    public async ValueTask DisposeAsync()
    {
        // Signalling first, which tells the server
        if (stopping.IsCancellationRequested) return;
        await signal.DisposeAsync();
        await stopping.CancelAsync();
        inbox.Writer.TryComplete();
        await processing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (publisherConnecting is not null) await publisherConnecting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (subscriberConnecting is not null) await subscriberConnecting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (Publisher is not null) await Publisher.DisposeAsync();
        // Done
        if (Subscriber is not null) await Subscriber.DisposeAsync();
    }

    /// <summary>Send an AddTrack, and return the track once the server published it.</summary>
    private async Task<TrackInfo> AddTrackAsync(AddTrackRequest request)
    {
        // Awaited by its cid
        TaskCompletionSource<TrackInfo> published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingTracks[request.Cid] = published;
        await signal.SendAsync(new SignalRequest { AddTrack = request }, stopping.Token);
        // Return track
        return await published.Task;
    }

    /// <summary>Handle the server's messages one at a time, in order.</summary>
    private async Task ProcessAsync()
    {
        // Until the signalling closes
        await foreach (SignalResponse message in inbox.Reader.ReadAllAsync())
        {
            try
            {
                await HandleAsync(message);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "LiveKit message {Message} failed", message.MessageCase);
            }
        }
    }

    /// <summary>Handle one message of the server.</summary>
    private async Task HandleAsync(SignalResponse message)
    {
        // By kind
        switch (message.MessageCase)
        {
            case SignalResponse.MessageOneofCase.Offer:
                await OnSubscriberOfferAsync(message.Offer);
                break;
            case SignalResponse.MessageOneofCase.Answer:
                publisherAnswer?.TrySetResult(message.Answer.Sdp);
                break;
            case SignalResponse.MessageOneofCase.Trickle:
                OnTrickle(message.Trickle);
                break;
            case SignalResponse.MessageOneofCase.TrackPublished:
                if (pendingTracks.TryRemove(message.TrackPublished.Cid, out TaskCompletionSource<TrackInfo>? pending)) pending.TrySetResult(message.TrackPublished.Track);
                break;
            case SignalResponse.MessageOneofCase.Update:
                foreach (ParticipantInfo participant in message.Update.Participants) OnParticipant(participant);
                break;
            case SignalResponse.MessageOneofCase.Leave:
                logger.LogInformation("LiveKit asked to leave room {Room}: {Reason}", Name, message.Leave.Reason);
                Closed?.Invoke();
                break;
        }
    }

    /// <summary>Note a participant's change; one that left is removed.</summary>
    private void OnParticipant(ParticipantInfo participant)
    {
        // Not this side
        if (participant.Sid == LocalParticipant.Sid) return;
        if (participant.State == ParticipantInfo.Types.State.Disconnected) participants.TryRemove(participant.Sid, out _);
        else participants[participant.Sid] = participant;
        // Done
        ParticipantChanged?.Invoke(participant);
    }

    /// <summary>Answer the server's offer of the subscriber connection, creating it on the first, and note whose each stream is.</summary>
    private async Task OnSubscriberOfferAsync(SessionDescription offer)
    {
        // The streams of the offer, then the answer
        NoteStreams(offer.Sdp);
        string answer;
        if (Subscriber is not AudioPeerConnection subscriber)
        {
            subscriber = new AudioPeerConnection(new PeerConnectionOptions { Servers = servers, IncludeLoopback = options.IncludeLoopback, RelayOnly = options.RelayOnly, ReceiveVideo = options.ReceiveVideo });
            Subscriber = subscriber;
            subscriber.CandidateGathered += candidate => _ = TrickleAsync(subscriber, candidate, SignalTarget.Subscriber);
            SubscriberCreated?.Invoke(subscriber);
            answer = subscriber.AcceptOffer(offer.Sdp);
            lock (candidateLock)
            {
                foreach (string candidate in earlySubscriberCandidates) subscriber.AddRemoteCandidate(candidate);
                earlySubscriberCandidates.Clear();
            }
            subscriberConnecting = subscriber.ConnectAsync(stopping.Token);
        }
        else answer = subscriber.AcceptRestartOffer(offer.Sdp);
        // Done
        await signal.SendAsync(new SignalRequest { Answer = new SessionDescription { Type = "answer", Sdp = answer, Id = offer.Id } }, stopping.Token);
    }

    /// <summary>Note the participant of each stream of an offer, by the SSRCs and the <c>msid</c> of its sections.</summary>
    private void NoteStreams(string sdp)
    {
        // Each section's stream ID, PA_…|TR_…, and its SSRCs
        foreach (Uniform.Matrix.WebRtc.Sdp.MediaDescription media in Uniform.Matrix.WebRtc.Sdp.SessionDescription.Parse(sdp).Media)
        {
            string? msid = media.Get("msid") ?? media.GetAll("ssrc").Select(s => s.Split(' ', 2)).Where(s => s.Length == 2 && s[1].StartsWith("msid:", StringComparison.Ordinal)).Select(s => s[1][5..]).FirstOrDefault();
            if (msid?.Split(' ')[0].Split('|') is not [string participantSid, ..] || participantSid.Length == 0) continue;
            foreach (string ssrc in media.GetAll("ssrc").Select(s => s.Split(' ')[0]).Distinct())
            {
                if (uint.TryParse(ssrc, CultureInfo.InvariantCulture, out uint value)) streams[value] = participantSid;
            }
        }
    }

    /// <summary>Give a candidate the server trickled to its connection; the subscriber's that come before its offer wait.</summary>
    private void OnTrickle(TrickleRequest trickle)
    {
        // The candidate line of its JSON
        if (trickle.CandidateInit.Length == 0 || JsonNode.Parse(trickle.CandidateInit)?["candidate"]?.GetValue<string>() is not string candidate || candidate.Length == 0) return;
        if (trickle.Target == SignalTarget.Publisher)
        {
            Publisher?.AddRemoteCandidate(candidate);
            return;
        }
        lock (candidateLock)
        {
            if (Subscriber is null)
            {
                earlySubscriberCandidates.Add(candidate);
                return;
            }
        }
        // Done
        Subscriber.AddRemoteCandidate(candidate);
    }

    /// <summary>Trickle a local candidate of a connection to the server.</summary>
    private Task TrickleAsync(AudioPeerConnection connection, IceCandidate candidate, SignalTarget target)
    {
        // As a browser's RTCIceCandidateInit
        string init = new JsonObject { ["candidate"] = candidate.ToString(), ["sdpMid"] = connection.BundleMid ?? "0", ["sdpMLineIndex"] = connection.BundleIndex }.ToJsonString();
        // Done
        return signal.SendAsync(new SignalRequest { Trickle = new TrickleRequest { CandidateInit = init, Target = target } }, stopping.Token);
    }

    /// <summary>Return null for an empty string.</summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
