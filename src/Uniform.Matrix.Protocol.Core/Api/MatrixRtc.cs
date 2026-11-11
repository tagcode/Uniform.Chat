namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

/// <summary>A <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4143">MatrixRTC</see> transport: how media goes, e.g. a LiveKit SFU behind its authorization service.</summary>
public sealed record RtcTransport
{
    /// <summary>The LiveKit transport's type.</summary>
    public const string LiveKit = "livekit";
    /// <summary>Kind, e.g. <see cref="LiveKit"/>.</summary>
    public required string Type { get; init; }
    /// <summary>The LiveKit authorization service, which exchanges an OpenID token for a LiveKit token, e.g. <c>https://example.org/livekit/jwt</c>.</summary>
    public string? LivekitServiceUrl { get; init; }
    /// <summary>The LiveKit room, the Matrix room's ID; still read by some clients, though services derive the room themselves.</summary>
    public string? LivekitAlias { get; init; }
}

/// <summary>Response of <c>GET /_matrix/client/unstable/org.matrix.msc4143/rtc/transports</c>: the homeserver's transports, preferred first.</summary>
public sealed record RtcTransportsResponse
{
    /// <summary>The transports.</summary>
    public IReadOnlyList<RtcTransport> RtcTransports { get; init; } = [];
}

/// <summary>The focus a member uses, in <see cref="CallMemberContent.FocusActive"/>: LiveKit, each member on the SFU of its own homeserver.</summary>
public sealed record RtcFocusActive
{
    /// <summary>Kind, <see cref="RtcTransport.LiveKit"/>.</summary>
    public string Type { get; init; } = RtcTransport.LiveKit;
    /// <summary>How the SFU is chosen; <c>multi_sfu</c>: each publishes on its own, and subscribes on each other's.</summary>
    public string FocusSelection { get; init; } = "multi_sfu";
}

/// <summary>Content of <see cref="EventTypes.CallMember"/>, a device's membership in a room's call, as Element Call writes it; empty when the device left.</summary>
/// <remarks>The state key is <c>_{user}_{device}_m.call</c>. A membership is valid until <see cref="CreatedTs"/>, or the event's own time, plus <see cref="Expires"/>; a delayed event of empty content ends it when the device disappears.</remarks>
public sealed record CallMemberContent
{
    /// <summary>The application, <c>m.call</c> for calls.</summary>
    public string? Application { get; init; }
    /// <summary>The call within the room; empty for the room's one call.</summary>
    public string? CallId { get; init; }
    /// <summary><c>m.room</c> for the room's call.</summary>
    public string? Scope { get; init; }
    /// <summary>The member's device.</summary>
    public string? DeviceId { get; init; }
    /// <summary>How the member chooses its SFU.</summary>
    public RtcFocusActive? FocusActive { get; init; }
    /// <summary>The transports the member publishes on, its own homeserver's first.</summary>
    public IReadOnlyList<RtcTransport>? FociPreferred { get; init; }
    /// <summary>When the member joined, in milliseconds since the epoch, when this is an update of its membership.</summary>
    public long? CreatedTs { get; init; }
    /// <summary>Milliseconds after joining that the membership ends unless updated.</summary>
    public long? Expires { get; init; }
    /// <summary>The call's intent, <c>audio</c> or <c>video</c>.</summary>
    [JsonPropertyName("m.call.intent")]
    public string? CallIntent { get; init; }
    /// <summary>The member's identity on the SFU, <c>{user}:{device}</c>.</summary>
    [JsonPropertyName("membershipID")]
    public string? MembershipId { get; init; }

    /// <summary>Whether this is a membership, not a leave's empty content.</summary>
    [JsonIgnore]
    public bool IsJoined => Application is not null && DeviceId is not null;
}

/// <summary>Content of <see cref="EventTypes.RtcNotification"/>: a call started, ringing the others' devices for <see cref="Lifetime"/>.</summary>
public sealed record RtcNotificationContent
{
    /// <summary>Who is notified; the whole room in a direct chat.</summary>
    [JsonPropertyName("m.mentions")]
    public Mentions? Mentions { get; init; }
    /// <summary><c>ring</c> to ring, <c>notification</c> to only notify.</summary>
    public required string NotificationType { get; init; }
    /// <summary>The caller's membership event, as a reference.</summary>
    [JsonPropertyName("m.relates_to")]
    public Relation? RelatesTo { get; init; }
    /// <summary>When the caller sent it, by its clock, in milliseconds since the epoch.</summary>
    public long SenderTs { get; init; }
    /// <summary>Milliseconds it rings for.</summary>
    public long Lifetime { get; init; }
    /// <summary>The call's slot, <c>m.call#ROOM</c>.</summary>
    public string? SlotId { get; init; }
    /// <summary>The call's intent, <c>audio</c> or <c>video</c>.</summary>
    [JsonPropertyName("m.call.intent")]
    public string? CallIntent { get; init; }
}

/// <summary>Content of <see cref="EventTypes.RtcDecline"/>: the callee declined the call of a notification.</summary>
public sealed record RtcDeclineContent
{
    /// <summary>The declined notification, as a reference.</summary>
    [JsonPropertyName("m.relates_to")]
    public required Relation RelatesTo { get; init; }
}

/// <summary>A media key of a call member, in <see cref="CallEncryptionKeysContent"/>.</summary>
public sealed record CallMediaKey
{
    /// <summary>The key's index, 0 to 255, which each encrypted frame names.</summary>
    public int Index { get; init; }
    /// <summary>The raw key, in unpadded Base64.</summary>
    public required string Key { get; init; }
}

/// <summary>The sender of <see cref="CallEncryptionKeysContent"/>.</summary>
public sealed record CallKeyMember
{
    /// <summary>The member's ID, as <see cref="CallMemberContent.MembershipId"/>.</summary>
    public string? Id { get; init; }
    /// <summary>The device the member claims to call from.</summary>
    public required string ClaimedDeviceId { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallEncryptionKeys"/>, sent to each member's device over Olm: the sender's key that its media is encrypted with.</summary>
public sealed record CallEncryptionKeysContent
{
    /// <summary>The call's room.</summary>
    public required string RoomId { get; init; }
    /// <summary>The sender's member ID.</summary>
    public string? MemberId { get; init; }
    /// <summary>The key.</summary>
    public CallMediaKey? MediaKey { get; init; }
    /// <summary>The key again, as older clients read it.</summary>
    public CallMediaKey? Keys { get; init; }
    /// <summary>The sender's device.</summary>
    public required CallKeyMember Member { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3useruseridopenidrequest_token">POST /user/{userId}/openid/request_token</see>: a token that proves the user to another service.</summary>
public sealed record OpenIdTokenResponse
{
    /// <summary>The token.</summary>
    public required string AccessToken { get; init; }
    /// <summary><c>Bearer</c>.</summary>
    public string TokenType { get; init; } = "Bearer";
    /// <summary>The homeserver to verify the token with.</summary>
    public required string MatrixServerName { get; init; }
    /// <summary>Seconds the token holds.</summary>
    public long ExpiresIn { get; init; }
}

/// <summary>Response of sending a <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4140">delayed event</see>.</summary>
public sealed record DelayedEventResponse
{
    /// <summary>The delayed event's ID, to restart, cancel or send it.</summary>
    public required string DelayId { get; init; }
}
