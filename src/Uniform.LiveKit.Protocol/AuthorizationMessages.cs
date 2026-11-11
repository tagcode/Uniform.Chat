namespace Uniform.LiveKit.Protocol;

using System.Text.Json.Serialization;

/// <summary>A Matrix OpenID token, as <c>/_matrix/client/v3/user/{userId}/openid/request_token</c> returns it, which proves the user to a service through the user's homeserver.</summary>
public sealed record OpenIdToken
{
    /// <summary>The token the homeserver checks.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }
    /// <summary>Always <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }
    /// <summary>The homeserver that issued it, and that the service asks.</summary>
    [JsonPropertyName("matrix_server_name")]
    public string? MatrixServerName { get; init; }
    /// <summary>Seconds the token holds.</summary>
    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; }
}

/// <summary>Fields of a delayed leave handed to the service: it keeps the member's delayed leave event (MSC4140) from going off while the member is on LiveKit, and sends it once the member is gone, so the member's device need not restart it often.</summary>
public abstract record DelayedLeave
{
    /// <summary>Id of the member's delayed leave event (MSC4140).</summary>
    [JsonPropertyName("delay_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DelayId { get; init; }
    /// <summary>Timeout of the delayed event, in milliseconds.</summary>
    [JsonPropertyName("delay_timeout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DelayTimeout { get; init; }
    /// <summary>The member's client-server API base URL, where the delayed event is sent.</summary>
    [JsonPropertyName("delay_cs_api_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DelayClientServerApiUrl { get; init; }
}

/// <summary>Body of the legacy <c>POST /sfu/get</c>: a token for the room's call.</summary>
public sealed record LegacySfuRequest : DelayedLeave
{
    /// <summary>The Matrix room.</summary>
    [JsonPropertyName("room")]
    public string? Room { get; init; }
    /// <summary>The user's OpenID token.</summary>
    [JsonPropertyName("openid_token")]
    public OpenIdToken? OpenIdToken { get; init; }
    /// <summary>The user's device.</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }
}

/// <summary>The call member a <see cref="TokenRequest"/> is for, as in its membership event.</summary>
public sealed record CallMember
{
    /// <summary>The member's id in the membership event.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }
    /// <summary>The user the member claims to be, checked against the OpenID token.</summary>
    [JsonPropertyName("claimed_user_id")]
    public string? ClaimedUserId { get; init; }
    /// <summary>The device the member claims.</summary>
    [JsonPropertyName("claimed_device_id")]
    public string? ClaimedDeviceId { get; init; }
}

/// <summary>Body of <c>POST /get_token</c>: a token for slot <see cref="SlotId"/> of a room.</summary>
public sealed record TokenRequest : DelayedLeave
{
    /// <summary>The Matrix room.</summary>
    [JsonPropertyName("room_id")]
    public string? RoomId { get; init; }
    /// <summary>The call slot, e.g. <see cref="MatrixRtcNames.RoomCallSlot"/>.</summary>
    [JsonPropertyName("slot_id")]
    public string? SlotId { get; init; }
    /// <summary>The user's OpenID token.</summary>
    [JsonPropertyName("openid_token")]
    public OpenIdToken? OpenIdToken { get; init; }
    /// <summary>The call member.</summary>
    [JsonPropertyName("member")]
    public CallMember? Member { get; init; }
}

/// <summary>Answer of both requests: where LiveKit is and the token to join with.</summary>
public sealed record SfuResponse
{
    /// <summary>LiveKit's WebSocket URL, e.g. <c>wss://livekit.example.org</c>.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
    /// <summary>The LiveKit access token.</summary>
    [JsonPropertyName("jwt")]
    public string Jwt { get; init; } = "";
}

/// <summary>A failure answer, in Matrix style.</summary>
public sealed record AuthorizationError
{
    /// <summary>Error code, e.g. <see cref="Forbidden"/>.</summary>
    [JsonPropertyName("errcode")]
    public string ErrorCode { get; init; } = "";
    /// <summary>Human-readable reason.</summary>
    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    /// <summary>The body is not a valid request.</summary>
    public const string BadJson = "M_BAD_JSON";
    /// <summary>The user may not.</summary>
    public const string Forbidden = "M_FORBIDDEN";
    /// <summary>The OpenID token did not verify, or the claimed user is not its user.</summary>
    public const string Unauthorized = "M_UNAUTHORIZED";
    /// <summary>Another failure, e.g. LiveKit could not be reached.</summary>
    public const string Unknown = "M_UNKNOWN";
    /// <summary>No such endpoint.</summary>
    public const string Unrecognized = "M_UNRECOGNIZED";
}
