namespace Uniform.Matrix.Calls;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Uniform.LiveKit;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Video;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>Matrix 1:1 calls of the logged-in user: places calls, raises incoming ones, and routes the events of the synced timelines to their <see cref="VoiceCall"/>; sends through the outbox, which encrypts in encrypted rooms.</summary>
/// <remarks>
/// Register it as a singleton beside the client engine; it follows <see cref="SyncProcessor.Applied"/> from construction. TURN credentials come from the homeserver, and are kept until shortly before they expire.
/// Calls go over MatrixRTC, as <see cref="RtcCall"/>, when the homeserver has a LiveKit transport, as Element X can only take those; else as <see cref="LegacyCall"/> of <c>m.call.*</c> events. Both kinds of incoming call ring.
/// </remarks>
public sealed class CallService : IAsyncDisposable
{
    private readonly SyncProcessor processor;
    private readonly OutboxSender outbox;
    private readonly ClientSession session;
    private readonly NetworkMonitor network;
    private readonly ILogger<CallService> logger;
    private static readonly HttpClient AuthorizationHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CryptoService? crypto;
    private readonly ConcurrentDictionary<string, VoiceCall> calls = new();
    private IReadOnlyList<RtcTransport>? transports;
    private DateTimeOffset transportsFetched;
    private readonly ConcurrentDictionary<string, WaitingInvite> waiting = new();
    private readonly string partyId = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 10);
    private IReadOnlyList<IceServer> turnServers = [];
    private DateTimeOffset turnExpires;

    /// <summary>Follow the synced timelines for call events, and the to-device messages for MatrixRTC's keys.</summary>
    public CallService(SyncProcessor processor, OutboxSender outbox, ClientSession session, NetworkMonitor network, ILogger<CallService> logger, CryptoService? crypto = null)
    {
        // Dependencies, the sync hook, keys, and network changes
        this.processor = processor;
        this.outbox = outbox;
        this.session = session;
        this.network = network;
        this.logger = logger;
        this.crypto = crypto;
        processor.Applied += OnApplied;
        network.Changed += OnNetworkChanged;
        if (crypto is not null) crypto.ToDeviceReceived += OnToDevice;
    }

    /// <summary>Whether to place calls over MatrixRTC when the homeserver has a LiveKit transport; on by default. Off places <c>m.call.*</c> calls only.</summary>
    public bool UseMatrixRtc { get; set; } = true;

    /// <summary>Open the microphone and speaker of a call; the platform's devices by default, which Android replaces.</summary>
    public Func<IAudioDevices?> Devices { get; set; } = () => AudioDevices.ForThisPlatform();

    /// <summary>Opens the platform's camera, for calls answered with video; null where there is none, as on desktops for now.</summary>
    public Func<IVideoCamera?>? Camera { get; set; }

    /// <summary>Makes a decoder of a peer's video, e.g. the platform's hardware one; null for the C# decoder, as on desktops.</summary>
    public Func<IVideoDecoder?>? VideoDecoder { get; set; }

    /// <summary>Whether calls go through TURN only, so the peer does not learn this device's addresses.</summary>
    public bool RelayOnly { get; set; }

    /// <summary>Whether to accept the video a caller offers, to show it; this side sends none. On by default.</summary>
    public bool ReceiveVideo { get; set; } = true;

    /// <summary>Raised for a new incoming call that rings.</summary>
    public event Action<VoiceCall>? IncomingCall;

    /// <summary>Raised when a call changes state or mute; on any thread.</summary>
    public event Action<VoiceCall>? CallChanged;

    /// <summary>The calls not yet ended.</summary>
    public IReadOnlyList<VoiceCall> ActiveCalls => [.. calls.Values.Where(c => c.State != CallState.Ended)];

    /// <summary>Return the call in a room that has not ended, or null.</summary>
    public VoiceCall? FindCall(string roomId) => calls.Values.FirstOrDefault(c => c.RoomId == roomId && c.State != CallState.Ended);

    /// <summary>Call the other member of a room with two members, e.g. a direct chat.</summary>
    /// <exception cref="InvalidOperationException">When the room is unknown, has not exactly one other member, or already has a call.</exception>
    public Task<VoiceCall> PlaceCallAsync(string roomId, CancellationToken cancellationToken = default) => PlaceCallAsync(roomId, video: false, cancellationToken);

    /// <summary>Call the other member of a direct room with video too: this side's camera, when <see cref="Camera"/> has one, and the peer's when it sends; or answer their call that rings, with video.</summary>
    public Task<VoiceCall> PlaceVideoCallAsync(string roomId, CancellationToken cancellationToken = default) => PlaceCallAsync(roomId, video: true, cancellationToken);

    /// <summary>Call, with or without video, or answer the call that rings.</summary>
    private async Task<VoiceCall> PlaceCallAsync(string roomId, bool video, CancellationToken cancellationToken)
    {
        // The one other member
        MatrixClient client = await session.WhenReady;
        string ownUserId = client.UserId!;
        string[] others = processor.Read(rooms => rooms?.TryGetRoom(roomId, out Room? room) == true ? room.Members.Values.Where(m => m.IsActive && m.UserId != ownUserId).Select(m => m.UserId).ToArray() : null)
            ?? throw new InvalidOperationException("The room is not known.");
        if (others.Length != 1) throw new InvalidOperationException("Voice calls need a room with exactly one other member.");

        // Their call rings already: answer it rather than call over it
        if (FindCall(roomId) is VoiceCall existing)
        {
            if (existing.State != CallState.Ringing) throw new InvalidOperationException("The room already has a call.");
            await (video ? existing.AnswerWithVideoAsync(cancellationToken) : existing.AnswerAsync(cancellationToken));
            return existing;
        }

        // A MatrixRTC call when the homeserver has LiveKit
        if (await RtcTransportAsync(client, cancellationToken) is RtcTransport transport)
        {
            RtcCall rtc = CreateRtc(client, roomId, others[0], transport, null, video, await OptionsAsync(client, cancellationToken));
            try
            {
                await rtc.StartAsync(cancellationToken);
            }
            catch
            {
                // On a call that could not start
                await rtc.DisposeAsync();
                throw;
            }
            CallChanged?.Invoke(rtc);
            return rtc;
        }

        // Else a legacy call, ringing
        LegacyCall call = Create(roomId, Guid.NewGuid().ToString("N"), ownUserId, others[0], null, await OptionsAsync(client, cancellationToken));
        await call.InviteAsync(cancellationToken, video);
        CallChanged?.Invoke(call);
        // Return the call
        return call;
    }

    /// <summary>Return the homeserver's LiveKit transport, fetched again every few minutes; null without one, or when <see cref="UseMatrixRtc"/> is off.</summary>
    private async Task<RtcTransport?> RtcTransportAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Cached for a while
        if (!UseMatrixRtc) return null;
        if (transports is null || DateTimeOffset.UtcNow - transportsFetched > TimeSpan.FromMinutes(5))
        {
            try
            {
                transports = await client.GetRtcTransportsAsync(cancellationToken);
                transportsFetched = DateTimeOffset.UtcNow;
            }
            catch (Exception e) when (e is MatrixException or HttpRequestException)
            {
                logger.LogWarning(e, "MatrixRTC transports not available");
                transports ??= [];
            }
        }
        // Return the first LiveKit one
        return transports.FirstOrDefault(t => t.Type == RtcTransport.LiveKit && t.LivekitServiceUrl is not null);
    }

    /// <summary>Hang up all calls, and stop following the timelines; a server that does not answer holds each hangup for a few seconds at most.</summary>
    public async ValueTask DisposeAsync()
    {
        // Each active call
        processor.Applied -= OnApplied;
        network.Changed -= OnNetworkChanged;
        if (crypto is not null) crypto.ToDeviceReceived -= OnToDevice;
        foreach (VoiceCall call in ActiveCalls)
        {
            try
            {
                await call.HangupAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or MatrixException or HttpRequestException or OperationCanceledException)
            {
                logger.LogInformation("Hanging up call {CallId} on stopping failed: {Error}", call.CallId, ex.Message);
            }
        }
    }

    /// <summary>Restart ICE of the connected calls on the new network, as their path on the old one is gone.</summary>
    private void OnNetworkChanged()
    {
        // Each call that has media
        foreach (VoiceCall call in ActiveCalls.Where(c => c.State is CallState.Connected or CallState.Reconnecting)) _ = call.RestartIceAsync("network changed");
    }

    /// <summary>Route the call events of a sync, and update the MatrixRTC calls of the rooms that changed.</summary>
    private void OnApplied(SyncChanges changes)
    {
        // Call events of each room, in order, off the sync loop
        foreach (TimelineBatch batch in changes.Timeline)
        {
            ClientEvent[] events = [.. batch.Events.Where(e => e.Type.StartsWith("m.call.", StringComparison.Ordinal))];
            if (events.Length > 0) _ = RouteAsync(batch.Room.RoomId, events);
            ClientEvent[] rtc = [.. batch.Events.Where(e => e.Type is EventTypes.RtcNotification or EventTypes.RtcDecline)];
            if (rtc.Length > 0) _ = RouteRtcAsync(batch.Room.RoomId, rtc);
        }
        // Done: memberships may have changed in the rooms with new events
        foreach (string roomId in changes.Timeline.Select(b => b.Room.RoomId).Concat(changes.Joined.Select(r => r.RoomId)).Distinct())
        {
            foreach (RtcCall call in calls.Values.OfType<RtcCall>().Where(c => c.RoomId == roomId && c.State != CallState.Ended)) _ = call.UpdateAsync();
        }
    }

    /// <summary>Give a member's media key to the MatrixRTC calls of its room.</summary>
    private void OnToDevice(DecryptedToDeviceEvent ev)
    {
        // Keys only
        if (ev.Type != EventTypes.CallEncryptionKeys || ev.Content["room_id"]?.GetValue<string>() is not string roomId) return;
        // Done
        foreach (RtcCall call in calls.Values.OfType<RtcCall>().Where(c => c.RoomId == roomId && c.State != CallState.Ended)) call.TakeKey(ev);
    }

    /// <summary>Ring for MatrixRTC notifications of calls the peer is in, and route declines to their calls.</summary>
    private async Task RouteRtcAsync(string roomId, ClientEvent[] events)
    {
        // Each in order
        MatrixClient client = await session.WhenReady;
        foreach (ClientEvent ev in events)
        {
            try
            {
                if (ev.Type == EventTypes.RtcDecline && ev.TryGetContent(out RtcDeclineContent? decline) && decline.RelatesTo.EventId is string declined)
                {
                    foreach (RtcCall call in calls.Values.OfType<RtcCall>().Where(c => c.RoomId == roomId)) await call.DeclinedAsync(ev.Sender, declined);
                    continue;
                }
                if (ev.Type == EventTypes.RtcNotification && ev.TryGetContent(out RtcNotificationContent? ring)) await RingRtcAsync(client, roomId, ev, ring);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "MatrixRTC event {EventId} in {RoomId} failed", ev.EventId, roomId);
            }
        }
    }

    /// <summary>Ring for a MatrixRTC notification that rings, from someone else, still valid, while its sender is in the room's call and no other call is.</summary>
    private async Task RingRtcAsync(MatrixClient client, string roomId, ClientEvent ev, RtcNotificationContent ring)
    {
        // A ring of another, not expired: by the sender's clock unless it is far off
        if (ring.NotificationType != "ring" || ev.Sender == client.UserId) return;
        long basis = ring.SenderTs - ev.OriginServerTs > 20_000 ? ev.OriginServerTs : ring.SenderTs;
        TimeSpan remaining = DateTimeOffset.FromUnixTimeMilliseconds(basis + Math.Min(ring.Lifetime, 120_000)) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            logger.LogInformation("MatrixRTC ring {EventId} ignored: expired {Ago} ago", ev.EventId, -remaining);
            return;
        }

        // The caller in the call, and no call here yet
        if (!ReadMembers(roomId).Any(m => m.UserId == ev.Sender))
        {
            logger.LogInformation("MatrixRTC ring {EventId} ignored: {Sender} is not in the call", ev.EventId, ev.Sender);
            return;
        }
        if (FindCall(roomId) is not null) return;
        if (await RtcTransportAsync(client, CancellationToken.None) is not RtcTransport transport)
        {
            logger.LogWarning("MatrixRTC ring {EventId} ignored: the homeserver has no LiveKit transport", ev.EventId);
            return;
        }

        // Ringing until answered, declined or expired
        RtcCall call = CreateRtc(client, roomId, ev.Sender, transport, ev.EventId, ring.CallIntent == "video", await OptionsAsync(client, CancellationToken.None));
        call.Ring(remaining);
        // Done
        IncomingCall?.Invoke(call);
    }

    /// <summary>Return the members' devices of a room's call now.</summary>
    private IReadOnlyList<(string UserId, string DeviceId, CallMemberContent Membership)> ReadMembers(string roomId) =>
        processor.Read(rooms => rooms?.TryGetRoom(roomId, out Room? room) == true ? RtcCall.Members(room, DateTimeOffset.UtcNow) : []) ?? [];

    /// <summary>Create and register a MatrixRTC call.</summary>
    private RtcCall CreateRtc(MatrixClient client, string roomId, string peerUserId, RtcTransport transport, string? notificationId, bool video, PeerConnectionOptions options)
    {
        // Its context: the client, the room's encryption, and LiveKit's options
        bool encrypted = processor.Read(rooms => rooms?.TryGetRoom(roomId, out Room? room) == true && room.IsEncrypted);
        RtcContext context = new()
        {
            Client = client,
            OwnUserId = client.UserId!,
            OwnDeviceId = client.DeviceId!,
            Transport = transport,
            SendEvent = (type, content, token) => outbox.SendNowAsync(client, roomId, type, content, token),
            SendToDevices = (type, content, devices, token) => crypto?.SendEncryptedToDevicesAsync(type, content, devices, token) ?? Task.FromResult(0),
            Encrypted = encrypted,
            RoomOptions = new LiveKitRoomOptions { Servers = options.Servers, RelayOnly = options.RelayOnly, ReceiveVideo = options.ReceiveVideo },
            Http = AuthorizationHttp,
            ReadMembers = () => ReadMembers(roomId),
        };
        RtcCall call = new(roomId, peerUserId, context, notificationId, video, () => Devices(), logger, () => Camera?.Invoke(), () => VideoDecoder?.Invoke());
        call.Changed += c => CallChanged?.Invoke(c);
        calls[call.CallId] = call;
        // Return call
        return call;
    }

    /// <summary>Route call events of a room to their calls, creating incoming ones for fresh invites.</summary>
    private async Task RouteAsync(string roomId, ClientEvent[] events)
    {
        // Each in order
        MatrixClient client = await session.WhenReady;
        string ownUserId = client.UserId!;
        foreach (ClientEvent ev in events)
        {
            try
            {
                if (Parse(ev) is not CallContent content)
                {
                    logger.LogInformation("Call event {EventId} of type {Type} in {RoomId} not understood", ev.EventId, ev.Type, roomId);
                    continue;
                }
                logger.LogInformation("Call event {Type} of call {CallId} from {Sender} in {RoomId}", ev.Type, content.CallId, ev.Sender, roomId);
                if (calls.TryGetValue(content.CallId, out VoiceCall? found) && found is LegacyCall call)
                {
                    await call.HandleAsync(ev.Sender, ev.Type, content);
                    continue;
                }
                if (content is CallHangupContent or CallRejectContent && waiting.TryGetValue(roomId, out WaitingInvite? kept) && kept.Invite.CallId == content.CallId)
                {
                    // A kept invite was withdrawn
                    waiting.TryRemove(roomId, out _);
                    continue;
                }
                if (content is CallInviteContent invite) await RingAsync(client, roomId, ownUserId, ev, invite);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Call event {EventId} in {RoomId} failed", ev.EventId, roomId);
            }
        }
    }

    /// <summary>Ring for an invite to this user that has not expired; when the room has a call with the same person, settle it as <see href="https://spec.matrix.org/latest/client-server-api/#glare">glare</see> or as the peer starting over.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Both called at once: the call with the lower call ID wins on both sides. When it is theirs, ours is hung up and theirs answered, as this user wanted to talk; when it is ours, theirs is kept, and rings if ours ends while it is still valid.</description></item>
    /// <item><description>Their earlier invite still rings: it is replaced by the new one, which rings.</description></item>
    /// <item><description>A call with them connects, is connected or reconnects: they started over, e.g. after it broke on their side; ours is hung up and theirs answered.</description></item>
    /// </list>
    /// </remarks>
    private async Task RingAsync(MatrixClient client, string roomId, string ownUserId, ClientEvent ev, CallInviteContent invite)
    {
        // From someone else, to this user, still valid
        if (ev.Sender == ownUserId || (invite.Invitee is not null && invite.Invitee != ownUserId)) return;
        TimeSpan age = TimeSpan.FromMilliseconds(ev.Unsigned?.Age ?? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ev.OriginServerTs));
        TimeSpan remaining = TimeSpan.FromMilliseconds(invite.Lifetime > 0 ? invite.Lifetime : LegacyCall.InviteLifetime.TotalMilliseconds) - age;
        if (remaining <= TimeSpan.Zero)
        {
            logger.LogInformation("Invite of call {CallId} ignored: expired {Ago} ago", invite.CallId, -remaining);
            return;
        }

        // A call in the room already: only with the same person, settled by its state
        bool answer = false;
        if (FindCall(roomId) is VoiceCall other)
        {
            if (other.PeerUserId != ev.Sender)
            {
                logger.LogInformation("Invite of call {CallId} ignored: the room has call {Other} with {Peer}", invite.CallId, other.CallId, other.PeerUserId);
                return;
            }
            if (other.State == CallState.Inviting && string.CompareOrdinal(invite.CallId, other.CallId) > 0)
            {
                // Glare, ours wins: keep theirs for when ours ends
                waiting[roomId] = new WaitingInvite(ev, invite, DateTimeOffset.UtcNow + remaining);
                logger.LogInformation("Both called: call {CallId} kept waiting, our call {Other} goes on", invite.CallId, other.CallId);
                return;
            }
            if (other.State == CallState.Ringing && other is LegacyCall ringing) await ringing.ReplaceAsync();
            else
            {
                // Glare that theirs wins, or they started over: ours ends, theirs is answered
                logger.LogInformation("Call {CallId} replaces call {Other} ({State})", invite.CallId, other.CallId, other.State);
                await other.HangupAsync();
                answer = true;
            }
        }

        // Ringing until answered or expired, or answered at once
        LegacyCall call = Create(roomId, invite.CallId, ownUserId, ev.Sender, invite, await OptionsAsync(client, CancellationToken.None));
        call.Ring(remaining);
        if (!answer)
        {
            IncomingCall?.Invoke(call);
            return;
        }
        // Done
        CallChanged?.Invoke(call);
        await call.AnswerAsync();
    }

    /// <summary>Ring for the invite kept waiting in a room, when the call that won over it ended and the invite is still valid.</summary>
    private async Task RingWaitingAsync(string roomId)
    {
        // A valid kept invite, and no other call
        if (!waiting.TryRemove(roomId, out WaitingInvite? kept) || FindCall(roomId) is not null) return;
        TimeSpan remaining = kept.Expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        MatrixClient client = await session.WhenReady;
        LegacyCall call = Create(roomId, kept.Invite.CallId, client.UserId!, kept.Event.Sender, kept.Invite, await OptionsAsync(client, CancellationToken.None));
        call.Ring(remaining);
        // Done
        logger.LogInformation("Call {CallId} that waited rings now", kept.Invite.CallId);
        IncomingCall?.Invoke(call);
    }

    /// <summary>An invite kept while this side's own call to the same person rang, as that call won the glare.</summary>
    /// <param name="Event">The invite event.</param>
    /// <param name="Invite">Its content.</param>
    /// <param name="Expires">When it stops being valid.</param>
    private sealed record WaitingInvite(ClientEvent Event, CallInviteContent Invite, DateTimeOffset Expires);

    /// <summary>Create and register a call.</summary>
    private LegacyCall Create(string roomId, string callId, string ownUserId, string peerUserId, CallInviteContent? invite, PeerConnectionOptions options)
    {
        // Events through the outbox, changes to the listeners
        LegacyCall call = new(roomId, callId, partyId, ownUserId, peerUserId, invite, (type, content, token) => outbox.EnqueueAsync(roomId, type, content, token), () => Devices(), options, logger, () => Camera?.Invoke(), () => VideoDecoder?.Invoke());
        call.Changed += c =>
        {
            CallChanged?.Invoke(c);
            if (c.State == CallState.Ended) _ = RingWaitingAsync(c.RoomId);
        };
        calls[callId] = call;
        // Return call
        return call;
    }

    /// <summary>Return the peer connection's options, with the homeserver's TURN servers, fetched again when about to expire.</summary>
    private async Task<PeerConnectionOptions> OptionsAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Cached credentials, else new ones; none when the server has no TURN
        if (DateTimeOffset.UtcNow >= turnExpires)
        {
            try
            {
                TurnServerResponse turn = await client.GetTurnServerAsync(cancellationToken);
                turnServers = [.. turn.Uris.Select(uri => new IceServer(uri, turn.Username, turn.Password))];
                turnExpires = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(60, (turn.Ttl ?? 3600) - 60));
            }
            catch (Exception e) when (e is MatrixException or HttpRequestException)
            {
                logger.LogWarning(e, "TURN servers not available; calls only connect directly");
            }
        }
        // Return options
        return new PeerConnectionOptions { Servers = turnServers, RelayOnly = RelayOnly, ReceiveVideo = ReceiveVideo };
    }

    /// <summary>Return a call event's content by its type, or null when malformed or not handled.</summary>
    private static CallContent? Parse(ClientEvent ev) => ev.Type switch
    {
        EventTypes.CallInvite => ev.TryGetContent(out CallInviteContent? content) ? content : null,
        EventTypes.CallCandidates => ev.TryGetContent(out CallCandidatesContent? content) ? content : null,
        EventTypes.CallAnswer => ev.TryGetContent(out CallAnswerContent? content) ? content : null,
        EventTypes.CallSelectAnswer => ev.TryGetContent(out CallSelectAnswerContent? content) ? content : null,
        EventTypes.CallReject => ev.TryGetContent(out CallRejectContent? content) ? content : null,
        EventTypes.CallHangup => ev.TryGetContent(out CallHangupContent? content) ? content : null,
        EventTypes.CallNegotiate => ev.TryGetContent(out CallNegotiateContent? content) ? content : null,
        _ => null,
    };
}
