namespace Uniform.Matrix.Calls;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encryption = global::LiveKit.Proto.Encryption;
using Microsoft.Extensions.Logging;
using Uniform.LiveKit;
using Uniform.LiveKit.Protocol;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Video;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>What a <see cref="RtcCall"/> needs of the client and its homeserver.</summary>
internal sealed record RtcContext
{
    /// <summary>The logged-in client, for state and delayed events and OpenID tokens.</summary>
    public required MatrixClient Client { get; init; }
    /// <summary>This user.</summary>
    public required string OwnUserId { get; init; }
    /// <summary>This device.</summary>
    public required string OwnDeviceId { get; init; }
    /// <summary>The homeserver's LiveKit transport, that this side publishes on.</summary>
    public required RtcTransport Transport { get; init; }
    /// <summary>Sends a room event at once, encrypted in encrypted rooms, and returns its ID.</summary>
    public required Func<string, object, CancellationToken, Task<string>> SendEvent { get; init; }
    /// <summary>Sends a to-device event to devices over Olm, and returns how many it reached.</summary>
    public required Func<string, JsonObject, IReadOnlyCollection<(string UserId, string DeviceId)>, CancellationToken, Task<int>> SendToDevices { get; init; }
    /// <summary>Whether the room is encrypted, so the media are too.</summary>
    public required bool Encrypted { get; init; }
    /// <summary>Options of the LiveKit connections, with the homeserver's TURN servers.</summary>
    public required LiveKitRoomOptions RoomOptions { get; init; }
    /// <summary>Client of the authorization services.</summary>
    public required HttpClient Http { get; init; }
    /// <summary>Returns the members' devices of the room's call now, from the synced state.</summary>
    public required Func<IReadOnlyList<(string UserId, string DeviceId, CallMemberContent Membership)>> ReadMembers { get; init; }
}

/// <summary>A 1:1 call over <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4143">MatrixRTC</see>, as Element X and Element Call make them: each side's membership in the room's call as a state event, a ring notification, the media through LiveKit SFUs, and the frames encrypted with keys sent to each member's device.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Joining: an OpenID token buys a LiveKit token from the homeserver's authorization service, this side publishes on its SFU, and writes its membership, <c>_{user}_{device}_m.call</c>, with a delayed event that empties it unless restarted every few seconds.</description></item>
/// <item><description>The caller rings the room with <c>rtc.notification</c>; the callee declines with <c>rtc.decline</c>, or answers by joining.</description></item>
/// <item><description>Each side sends its media key in <c>io.element.call.encryption_keys</c> to the other's call devices over Olm, and subscribes to the other on its SFU, the same one when they share a homeserver.</description></item>
/// <item><description>The call ends when the peer's membership goes, empty or expired.</description></item>
/// </list>
/// </remarks>
public sealed class RtcCall : VoiceCall
{
    /// <summary>How long the caller rings.</summary>
    public static readonly TimeSpan RingLifetime = TimeSpan.FromSeconds(60);
    /// <summary>How long after the device disappears its membership ends, as Element Call has it.</summary>
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(18);
    /// <summary>How often the delayed leave is restarted.</summary>
    public static readonly TimeSpan LeaveRestart = TimeSpan.FromSeconds(4);
    /// <summary>How long a membership holds unless renewed.</summary>
    public static readonly TimeSpan MembershipExpiry = TimeSpan.FromHours(4);
    /// <summary>The application and slot of a room's call.</summary>
    private const string Application = "m.call";
    private readonly RtcContext context;
    private readonly string? notificationId;
    private readonly HashSet<string> keyedDevices = [];
    private readonly Dictionary<string, (byte Index, byte[] Key)> earlyKeys = [];
    private readonly Dictionary<string, LiveKitRoom> otherSfus = [];
    private readonly byte[] ownKey = RandomNumberGenerator.GetBytes(32);
    private LiveKitRoom? room;
    private LiveKitMedia? media;
    private string? delayId;
    private string? ownNotificationId;
    private bool peerSeen;
    private bool joined;

    /// <summary>Create a call; <see cref="CallService"/> does.</summary>
    /// <param name="roomId">Room of the call.</param>
    /// <param name="peerUserId">The other user.</param>
    /// <param name="context">The client and the homeserver's transport.</param>
    /// <param name="notificationId">The peer's ring notification of an incoming call; null for an outgoing one.</param>
    /// <param name="video">Whether the call has video, as the caller's intent says.</param>
    /// <param name="devices">Opens the microphone and speaker.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="camera">Opens the camera.</param>
    /// <param name="videoDecoder">Makes the decoder of the peer's video.</param>
    internal RtcCall(string roomId, string peerUserId, RtcContext context, string? notificationId, bool video, Func<IAudioDevices?> devices, ILogger logger, Func<IVideoCamera?>? camera, Func<IVideoDecoder?>? videoDecoder)
        : base(roomId, "rtc-" + Guid.NewGuid().ToString("N"), peerUserId, devices, logger, camera, videoDecoder)
    {
        // Ringing when incoming, else inviting
        this.context = context;
        this.notificationId = notificationId;
        IsVideo = video;
        if (notificationId is null) SetState(CallState.Inviting);
    }

    /// <summary>The state key of this device's membership.</summary>
    private string StateKey => MembershipStateKey(context.OwnUserId, context.OwnDeviceId);

    /// <summary>This side's identity on the SFU, which the legacy authorization endpoint gives.</summary>
    private string OwnIdentity => MatrixRtcNames.LegacyIdentity(context.OwnUserId, context.OwnDeviceId);

    /// <inheritdoc/>
    public override bool IsIncoming => notificationId is not null;

    /// <summary>The path of the media to this side's SFU, once connected.</summary>
    public override (IceCandidate? Local, IceCandidate? Remote) Path => (room?.Publisher?.Ice?.SelectedLocal, room?.Publisher?.Ice?.SelectedRemote);

    /// <summary>Return the state key of a device's membership in a room's call.</summary>
    public static string MembershipStateKey(string userId, string deviceId) => $"_{userId}_{deviceId}_{Application}";

    /// <summary>Return the members' devices of a room's call, without expired ones; their memberships by user and device.</summary>
    public static IReadOnlyList<(string UserId, string DeviceId, CallMemberContent Membership)> Members(Room room, DateTimeOffset now)
    {
        // Joined memberships of the room's call, not expired
        List<(string, string, CallMemberContent)> members = [];
        foreach (MatrixEvent state in room.GetStates(EventTypes.CallMember).Values)
        {
            if (state is not ClientEvent ev || !ev.TryGetContent(out CallMemberContent? content) || !content.IsJoined || content.Application != Application || (content.CallId ?? "") != "") continue;
            long expires = (content.CreatedTs ?? ev.OriginServerTs) + (content.Expires ?? (long)MembershipExpiry.TotalMilliseconds);
            if (expires < now.ToUnixTimeMilliseconds()) continue;
            members.Add((ev.Sender, content.DeviceId!, content));
        }
        // Return members
        return members;
    }

    /// <summary>Join the call, and ring the room; ring until the peer joins or <see cref="RingLifetime"/> passes.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        // Joined, then the ring that refers to the membership
        string membershipId;
        await gate.WaitAsync(cancellationToken);
        try
        {
            membershipId = await JoinAsync(IsVideo, cancellationToken);
            RtcNotificationContent ring = new() { NotificationType = "ring", Mentions = new Mentions { UserIds = [], Room = true }, RelatesTo = new Relation { RelType = "m.reference", EventId = membershipId }, SenderTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Lifetime = (long)RingLifetime.TotalMilliseconds, SlotId = MatrixRtcNames.RoomCallSlot, CallIntent = IsVideo ? "video" : "audio" };
            ownNotificationId = await context.SendEvent(EventTypes.RtcNotification, ring, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
        // Done
        _ = TimeOutAsync(RingLifetime);
    }

    /// <summary>Ring an incoming call until <paramref name="remaining"/> passes, unless answered or the caller leaves.</summary>
    internal void Ring(TimeSpan remaining) => _ = TimeOutAsync(remaining);

    /// <inheritdoc/>
    private protected override async Task AnswerAsync(bool video, CancellationToken cancellationToken)
    {
        // Ringing only
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (State != CallState.Ringing) return;
            IsVideo |= video;
            SetState(CallState.Connecting);
            await JoinAsync(video, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // On a call that could not be joined
            logger.LogWarning(e, "Joining MatrixRTC call in {RoomId} failed", RoomId);
            _ = EndAsync("join_failed", byPeer: false);
        }
        finally
        {
            gate.Release();
        }
        // Done: the caller is in the call already
        await UpdateAsync();
    }

    /// <inheritdoc/>
    public override async Task RejectAsync(CancellationToken cancellationToken = default)
    {
        // A decline of the ring, then ended
        if (State != CallState.Ringing) return;
        try { if (notificationId is not null) await context.SendEvent(EventTypes.RtcDecline, new RtcDeclineContent { RelatesTo = new Relation { RelType = "m.reference", EventId = notificationId } }, cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Decline of MatrixRTC call in {RoomId} not sent", RoomId); }
        // Done
        await EndAsync("rejected", byPeer: false);
    }

    /// <inheritdoc/>
    public override async Task HangupAsync(string reason = "user_hangup", CancellationToken cancellationToken = default)
    {
        // Leaving happens on ending
        if (State == CallState.Ended) return;
        // Done
        await EndAsync(reason, byPeer: false);
    }

    /// <summary>Follow the room's call after a sync: the peer's devices that joined get this side's key and are subscribed to, and the call ends when the peer left after joining, or the caller left while ringing.</summary>
    internal async Task UpdateAsync()
    {
        // The peer's devices in the call now
        await gate.WaitAsync();
        try
        {
            if (State == CallState.Ended) return;
            List<(string UserId, string DeviceId, CallMemberContent Membership)> peers = [.. context.ReadMembers().Where(m => m.UserId == PeerUserId)];
            if (peers.Count > 0) peerSeen = true;
            if (peers.Count == 0)
            {
                if (peerSeen || IsIncoming) await EndAsync("user_hangup", byPeer: true);
                return;
            }
            if (!joined) return;

            // Their key, their SFU, and connected
            await ShareKeyAsync(peers);
            foreach ((_, _, CallMemberContent membership) in peers) await SubscribeAsync(membership);
            if (State is CallState.Inviting or CallState.Connecting)
            {
                ConnectedAt = DateTimeOffset.UtcNow;
                logger.LogInformation("MatrixRTC call in {RoomId} connected with {Peer}", RoomId, PeerUserId);
                SetState(CallState.Connected);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Take a decline of this side's ring, by the peer or by another of this user's devices.</summary>
    internal async Task DeclinedAsync(string sender, string declinedId)
    {
        // This call's ring, or the ring this side got and declined elsewhere
        if (State == CallState.Ended) return;
        if (sender == PeerUserId && declinedId == ownNotificationId) await EndAsync("rejected", byPeer: true);
        else if (sender == context.OwnUserId && declinedId == notificationId && State == CallState.Ringing) await EndAsync("rejected_elsewhere", byPeer: false);
    }

    /// <summary>Take a media key a member sent, e.g. the peer's; kept until the media start.</summary>
    internal void TakeKey(DecryptedToDeviceEvent ev)
    {
        // This room's, of the peer
        if (ev.Sender != PeerUserId || ev.Content.Deserialize<CallEncryptionKeysContent>(MatrixJson.Options) is not { } content || content.RoomId != RoomId) return;
        if ((content.MediaKey ?? content.Keys) is not CallMediaKey key || key.Index is < 0 or > 255) return;
        byte[] raw;
        try { raw = Convert.FromBase64String(Pad(key.Key)); }
        catch (FormatException) { return; }
        string identity = MatrixRtcNames.LegacyIdentity(ev.Sender, content.Member.ClaimedDeviceId);
        logger.LogInformation("MatrixRTC key {Index} of {Identity} in {RoomId}", key.Index, identity, RoomId);
        lock (earlyKeys)
        {
            if (media is null)
            {
                earlyKeys[identity] = ((byte)key.Index, raw);
                return;
            }
        }
        // Done
        media.SetKey(identity, (byte)key.Index, raw);
    }

    /// <summary>Join the call: a LiveKit token, publish on the SFU, the membership with its delayed leave, and the media; return the membership's event ID.</summary>
    private async Task<string> JoinAsync(bool video, CancellationToken cancellationToken)
    {
        // A publisher's token from the homeserver's service
        string serviceUrl = context.Transport.LivekitServiceUrl ?? throw new InvalidOperationException("The homeserver's transport has no LiveKit service.");
        OpenIdTokenResponse openId = await context.Client.RequestOpenIdTokenAsync(cancellationToken);
        SfuResponse sfu = await new MatrixRtcAuthorizationClient(context.Http, serviceUrl).GetLegacyAsync(new LegacySfuRequest { Room = RoomId, DeviceId = context.OwnDeviceId, OpenIdToken = new OpenIdToken { AccessToken = openId.AccessToken, TokenType = openId.TokenType, MatrixServerName = openId.MatrixServerName, ExpiresIn = openId.ExpiresIn } }, cancellationToken);

        // Published, with the media on it
        room = await LiveKitRoom.ConnectAsync(sfu.Url, sfu.Jwt, context.RoomOptions, logger, cancellationToken);
        room.Closed += () => { if (State != CallState.Ended) _ = EndAsync("sfu_closed", byPeer: false); };
        await room.PublishAsync(video, context.Encrypted ? Encryption.Types.Type.Gcm : Encryption.Types.Type.None, cancellationToken: cancellationToken);
        LiveKitMedia opened = new(room, context.Encrypted);
        opened.OwnKey.SetKey(0, ownKey);
        opened.DecryptionFailed += identity => logger.LogDebug("MatrixRTC frame of {Identity} did not decrypt", identity);
        lock (earlyKeys)
        {
            media = opened;
            foreach ((string identity, (byte index, byte[] key)) in earlyKeys) opened.SetKey(identity, index, key);
            earlyKeys.Clear();
        }
        StartMedia(opened);
        StartVideo(opened);
        if (video) StartCamera(opened);

        // The membership, emptied after a delay unless restarted
        delayId = await context.Client.SendDelayedStateEventAsync(RoomId, EventTypes.CallMember, new { }, StateKey, LeaveDelay, cancellationToken);
        CallMemberContent membership = new() { Application = Application, CallId = "", Scope = "m.room", DeviceId = context.OwnDeviceId, FocusActive = new RtcFocusActive(), FociPreferred = [context.Transport with { LivekitAlias = RoomId }], Expires = (long)MembershipExpiry.TotalMilliseconds, CallIntent = IsVideo ? "video" : "audio", MembershipId = OwnIdentity };
        string membershipId = await context.Client.SendStateEventAsync(RoomId, EventTypes.CallMember, membership, StateKey, cancellationToken);
        joined = true;
        _ = KeepAliveAsync();
        logger.LogInformation("Joined MatrixRTC call in {RoomId} on {Url} as {Identity}, encrypted {Encrypted}", RoomId, sfu.Url, OwnIdentity, context.Encrypted);
        // Return membership
        return membershipId;
    }

    /// <summary>Send this side's key to the peer's devices that do not have it yet.</summary>
    private async Task ShareKeyAsync(IReadOnlyList<(string UserId, string DeviceId, CallMemberContent Membership)> peers)
    {
        // New devices only
        if (!context.Encrypted) return;
        (string, string)[] devices = [.. peers.Select(p => (p.UserId, p.DeviceId)).Where(d => !keyedDevices.Contains($"{d.UserId}|{d.DeviceId}"))];
        if (devices.Length == 0) return;
        CallMediaKey key = new() { Index = 0, Key = Convert.ToBase64String(ownKey) };
        CallEncryptionKeysContent content = new() { RoomId = RoomId, MemberId = OwnIdentity, MediaKey = key, Keys = key, Member = new CallKeyMember { Id = OwnIdentity, ClaimedDeviceId = context.OwnDeviceId } };
        int reached = await context.SendToDevices(EventTypes.CallEncryptionKeys, JsonSerializer.SerializeToNode(content, MatrixJson.Options)!.AsObject(), devices, ending.Token);
        foreach ((string userId, string deviceId) in devices) keyedDevices.Add($"{userId}|{deviceId}");
        // Done
        logger.LogInformation("MatrixRTC key sent to {Reached} of {Count} devices of {Peer}", reached, devices.Length, PeerUserId);
    }

    /// <summary>Subscribe to a member on its own SFU, unless it is this side's.</summary>
    private async Task SubscribeAsync(CallMemberContent membership)
    {
        // Another service than this side's
        if (membership.FociPreferred?.FirstOrDefault(f => f.Type == RtcTransport.LiveKit) is not { LivekitServiceUrl: string serviceUrl } || serviceUrl.TrimEnd('/') == context.Transport.LivekitServiceUrl?.TrimEnd('/') || otherSfus.ContainsKey(serviceUrl) || media is null) return;
        try
        {
            // A subscriber's token there, then its media
            OpenIdTokenResponse openId = await context.Client.RequestOpenIdTokenAsync(ending.Token);
            SfuResponse sfu = await new MatrixRtcAuthorizationClient(context.Http, serviceUrl).GetTokenAsync(new TokenRequest { RoomId = RoomId, SlotId = MatrixRtcNames.RoomCallSlot, OpenIdToken = new OpenIdToken { AccessToken = openId.AccessToken, TokenType = openId.TokenType, MatrixServerName = openId.MatrixServerName, ExpiresIn = openId.ExpiresIn }, Member = new CallMember { Id = Guid.NewGuid().ToString(), ClaimedUserId = context.OwnUserId, ClaimedDeviceId = context.OwnDeviceId } }, ending.Token);
            LiveKitRoom other = await LiveKitRoom.ConnectAsync(sfu.Url, sfu.Jwt, context.RoomOptions, logger, ending.Token);
            otherSfus[serviceUrl] = other;
            media.Subscribe(other);
            // Done
            logger.LogInformation("Subscribed to {Peer} on {Url}", PeerUserId, sfu.Url);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // On an SFU that could not be joined; the next sync tries again
            logger.LogWarning(e, "Subscribing on {Service} failed", serviceUrl);
        }
    }

    /// <summary>Restart the delayed leave until the call ends, making a new one when it was sent meanwhile.</summary>
    private async Task KeepAliveAsync()
    {
        // Every few seconds
        using PeriodicTimer timer = new(LeaveRestart);
        try
        {
            while (await timer.WaitForNextTickAsync(ending.Token))
            {
                try
                {
                    if (delayId is string id) await context.Client.UpdateDelayedEventAsync(id, "restart", ending.Token);
                }
                catch (MatrixException e) when (e.Errcode == ErrorCodes.NotFound)
                {
                    // The leave was sent: a new one, and the membership again
                    logger.LogWarning("Delayed leave of MatrixRTC call in {RoomId} was sent; joining again", RoomId);
                    delayId = await context.Client.SendDelayedStateEventAsync(RoomId, EventTypes.CallMember, new { }, StateKey, LeaveDelay, ending.Token);
                }
                catch (Exception e) when (e is HttpRequestException or MatrixException)
                {
                    // On a network error; the next tick tries again
                    logger.LogDebug(e, "Restarting delayed leave failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ended
        }
    }

    /// <summary>End an outgoing call that nobody joined in time, or stop ringing an incoming one.</summary>
    private async Task TimeOutAsync(TimeSpan lifetime)
    {
        // Unless answered or ended
        try { await Task.Delay(lifetime, ending.Token); }
        catch (OperationCanceledException) { return; }
        if (State is CallState.Inviting) await HangupAsync("invite_timeout");
        // Done
        else if (State is CallState.Ringing) await EndAsync("invite_timeout", byPeer: true);
    }

    /// <summary>Tell the others the microphone is muted, so they show it.</summary>
    private protected override void OnMuted(bool value) { if (room is not null) _ = room.MuteAsync(video: false, value); }

    /// <summary>Tell the others the camera is off, so they show its placeholder.</summary>
    private protected override void OnCameraOff(bool value) { if (room is not null) _ = room.MuteAsync(video: true, value); }

    /// <summary>Leave: the membership emptied at once by sending its delayed leave, and the SFUs left.</summary>
    private protected override async ValueTask OnEndedAsync()
    {
        // The leave, sent now; else an empty membership
        if (joined)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            try
            {
                if (delayId is string id) await context.Client.UpdateDelayedEventAsync(id, "send", timeout.Token);
                else await context.Client.SendStateEventAsync(RoomId, EventTypes.CallMember, new { }, StateKey, timeout.Token);
            }
            catch (Exception e) when (e is MatrixException or HttpRequestException or OperationCanceledException)
            {
                // On a leave that did not go; the delayed one goes later
                logger.LogInformation("Leaving MatrixRTC call in {RoomId} now failed: {Error}", RoomId, e.Message);
            }
        }
        foreach (LiveKitRoom other in otherSfus.Values) await other.DisposeAsync();
        // Done
        if (room is not null) await room.DisposeAsync();
    }

    /// <summary>Return Base64 with its padding, as keys may come without.</summary>
    private static string Pad(string base64) => base64.Replace('-', '+').Replace('_', '/') + new string('=', (4 - base64.Length % 4) % 4);
}
