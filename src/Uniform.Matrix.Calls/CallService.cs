namespace Uniform.Matrix.Calls;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>Matrix 1:1 voice calls of the logged-in user: places calls, raises incoming ones, and routes the m.call.* events of the synced timelines to their <see cref="VoiceCall"/>; sends through the outbox, which encrypts in encrypted rooms.</summary>
/// <remarks>Register it as a singleton beside the client engine; it follows <see cref="SyncProcessor.Applied"/> from construction. TURN credentials come from the homeserver, and are kept until shortly before they expire.</remarks>
public sealed class CallService : IAsyncDisposable
{
    private readonly SyncProcessor processor;
    private readonly OutboxSender outbox;
    private readonly ClientSession session;
    private readonly NetworkMonitor network;
    private readonly ILogger<CallService> logger;
    private readonly ConcurrentDictionary<string, VoiceCall> calls = new();
    private readonly ConcurrentDictionary<string, WaitingInvite> waiting = new();
    private readonly string partyId = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 10);
    private IReadOnlyList<IceServer> turnServers = [];
    private DateTimeOffset turnExpires;

    /// <summary>Follow the synced timelines for call events.</summary>
    public CallService(SyncProcessor processor, OutboxSender outbox, ClientSession session, NetworkMonitor network, ILogger<CallService> logger)
    {
        // Dependencies, the sync hook, and network changes
        this.processor = processor;
        this.outbox = outbox;
        this.session = session;
        this.network = network;
        this.logger = logger;
        processor.Applied += OnApplied;
        network.Changed += OnNetworkChanged;
    }

    /// <summary>Open the microphone and speaker of a call; the platform's devices by default, which Android replaces.</summary>
    public Func<IAudioDevices?> Devices { get; set; } = () => AudioDevices.ForThisPlatform();

    /// <summary>Whether calls go through TURN only, so the peer does not learn this device's addresses.</summary>
    public bool RelayOnly { get; set; }

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
    public async Task<VoiceCall> PlaceCallAsync(string roomId, CancellationToken cancellationToken = default)
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
            await existing.AnswerAsync(cancellationToken);
            return existing;
        }

        // A new call, ringing
        VoiceCall call = Create(roomId, Guid.NewGuid().ToString("N"), ownUserId, others[0], null, await OptionsAsync(client, cancellationToken));
        await call.InviteAsync(cancellationToken);
        CallChanged?.Invoke(call);
        // Return the call
        return call;
    }

    /// <summary>Hang up all calls, and stop following the timelines.</summary>
    public async ValueTask DisposeAsync()
    {
        // Each active call
        processor.Applied -= OnApplied;
        network.Changed -= OnNetworkChanged;
        foreach (VoiceCall call in ActiveCalls) await call.HangupAsync();
    }

    /// <summary>Restart ICE of the connected calls on the new network, as their path on the old one is gone.</summary>
    private void OnNetworkChanged()
    {
        // Each call that has media
        foreach (VoiceCall call in ActiveCalls.Where(c => c.State is CallState.Connected or CallState.Reconnecting)) _ = call.RestartIceAsync("network changed");
    }

    /// <summary>Route the call events of a sync.</summary>
    private void OnApplied(SyncChanges changes)
    {
        // Call events of each room, in order, off the sync loop
        foreach (TimelineBatch batch in changes.Timeline)
        {
            ClientEvent[] events = [.. batch.Events.Where(e => e.Type.StartsWith("m.call.", StringComparison.Ordinal))];
            if (events.Length > 0) _ = RouteAsync(batch.Room.RoomId, events);
        }
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
                if (calls.TryGetValue(content.CallId, out VoiceCall? call))
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
        TimeSpan remaining = TimeSpan.FromMilliseconds(invite.Lifetime > 0 ? invite.Lifetime : VoiceCall.InviteLifetime.TotalMilliseconds) - age;
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
            if (other.State == CallState.Ringing) await other.ReplaceAsync();
            else
            {
                // Glare that theirs wins, or they started over: ours ends, theirs is answered
                logger.LogInformation("Call {CallId} replaces call {Other} ({State})", invite.CallId, other.CallId, other.State);
                await other.HangupAsync();
                answer = true;
            }
        }

        // Ringing until answered or expired, or answered at once
        VoiceCall call = Create(roomId, invite.CallId, ownUserId, ev.Sender, invite, await OptionsAsync(client, CancellationToken.None));
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
        VoiceCall call = Create(roomId, kept.Invite.CallId, client.UserId!, kept.Event.Sender, kept.Invite, await OptionsAsync(client, CancellationToken.None));
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
    private VoiceCall Create(string roomId, string callId, string ownUserId, string peerUserId, CallInviteContent? invite, PeerConnectionOptions options)
    {
        // Events through the outbox, changes to the listeners
        VoiceCall call = new(roomId, callId, partyId, ownUserId, peerUserId, invite, (type, content, token) => outbox.EnqueueAsync(roomId, type, content, token), () => Devices(), options, logger);
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
        return new PeerConnectionOptions { Servers = turnServers, RelayOnly = RelayOnly };
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
