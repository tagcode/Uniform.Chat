namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3voipturnserver">GET /_matrix/client/v3/voip/turnServer</see>: time-limited TURN credentials; empty when the server has no TURN.</summary>
public sealed record TurnServerResponse
{
    /// <summary>Username.</summary>
    public string? Username { get; init; }
    /// <summary>Password.</summary>
    public string? Password { get; init; }
    /// <summary>TURN URIs, e.g. <c>turn:turn.example.org:3478?transport=udp</c>.</summary>
    public IReadOnlyList<string> Uris { get; init; } = [];
    /// <summary>Seconds the credentials hold.</summary>
    public long? Ttl { get; init; }
}

/// <summary>Fields every <see href="https://spec.matrix.org/latest/client-server-api/#voice-over-ip">call event</see> has.</summary>
public abstract record CallContent
{
    /// <summary>Version of the call protocol; <c>1</c> now, the number 0 from old clients.</summary>
    public const string CurrentVersion = "1";
    /// <summary>The call's ID, the same in all its events.</summary>
    public required string CallId { get; init; }
    /// <summary>The sending client's ID within the call, telling a user's devices apart; missing from version 0.</summary>
    public string? PartyId { get; init; }
    /// <summary>Version, as text; a number in version 0.</summary>
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string Version { get; init; } = CurrentVersion;
}

/// <summary>An SDP offer or answer.</summary>
public sealed record CallSessionDescription
{
    /// <summary><c>offer</c> or <c>answer</c>.</summary>
    public required string Type { get; init; }
    /// <summary>The SDP.</summary>
    public required string Sdp { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallInvite"/>.</summary>
public sealed record CallInviteContent : CallContent
{
    /// <summary>The offer.</summary>
    public required CallSessionDescription Offer { get; init; }
    /// <summary>Milliseconds the invite is valid after it was sent.</summary>
    public long Lifetime { get; init; }
    /// <summary>The user called, in rooms with more members; any member when null.</summary>
    public string? Invitee { get; init; }
}

/// <summary>An ICE candidate of <see cref="CallCandidatesContent"/>; an empty <see cref="Candidate"/> ends the candidates.</summary>
public sealed record CallCandidate
{
    /// <summary>The <c>candidate:</c> attribute.</summary>
    public required string Candidate { get; init; }
    /// <summary>Media ID of the section it belongs to.</summary>
    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }
    /// <summary>Index of the section it belongs to.</summary>
    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallCandidates"/>.</summary>
public sealed record CallCandidatesContent : CallContent
{
    /// <summary>The candidates.</summary>
    public required IReadOnlyList<CallCandidate> Candidates { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallAnswer"/>.</summary>
public sealed record CallAnswerContent : CallContent
{
    /// <summary>The answer.</summary>
    public required CallSessionDescription Answer { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallSelectAnswer"/>.</summary>
public sealed record CallSelectAnswerContent : CallContent
{
    /// <summary>Party ID of the answer chosen.</summary>
    public required string SelectedPartyId { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallNegotiate"/>: a new offer or its answer during a call, e.g. to restart ICE after a network change.</summary>
public sealed record CallNegotiateContent : CallContent
{
    /// <summary>The offer or answer.</summary>
    public required CallSessionDescription Description { get; init; }
    /// <summary>Milliseconds an offer is valid after it was sent.</summary>
    public long? Lifetime { get; init; }
}

/// <summary>Content of <see cref="EventTypes.CallReject"/>.</summary>
public sealed record CallRejectContent : CallContent;

/// <summary>Content of <see cref="EventTypes.CallHangup"/>.</summary>
public sealed record CallHangupContent : CallContent
{
    /// <summary>Why, e.g. <c>user_hangup</c>, <c>ice_failed</c> or <c>invite_timeout</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>Reads a JSON string or number as text, as call versions are either; writes text.</summary>
public sealed class StringOrNumberConverter : JsonConverter<string>
{
    /// <summary>Read a string, or a number as its text.</summary>
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? (reader.TryGetInt64(out long number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)) : reader.GetString() ?? "";

    /// <summary>Write text.</summary>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
