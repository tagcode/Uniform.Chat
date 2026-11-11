namespace Uniform.LiveKit.Protocol;

using System.Text.Json.Serialization;

/// <summary>What an access token lets its holder do in LiveKit rooms, the <c>video</c> claim; unset permissions are left out of the token, and LiveKit reads a missing publish or subscribe permission as allowed.</summary>
/// <see href="https://docs.livekit.io/home/get-started/authentication/"/>
public sealed record VideoGrant
{
    /// <summary>Allow creating rooms.</summary>
    [JsonPropertyName("roomCreate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RoomCreate { get; init; }
    /// <summary>Allow listing rooms.</summary>
    [JsonPropertyName("roomList"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RoomList { get; init; }
    /// <summary>Allow managing <see cref="Room"/>, e.g. removing participants.</summary>
    [JsonPropertyName("roomAdmin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RoomAdmin { get; init; }
    /// <summary>Allow joining <see cref="Room"/>.</summary>
    [JsonPropertyName("roomJoin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RoomJoin { get; init; }
    /// <summary>Name of the room the grant is for.</summary>
    [JsonPropertyName("room"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Room { get; init; }
    /// <summary>Allow publishing audio and video.</summary>
    [JsonPropertyName("canPublish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanPublish { get; init; }
    /// <summary>Allow subscribing to others' tracks.</summary>
    [JsonPropertyName("canSubscribe"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanSubscribe { get; init; }
    /// <summary>Allow sending data messages.</summary>
    [JsonPropertyName("canPublishData"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanPublishData { get; init; }
    /// <summary>Allow changing one's own name and metadata.</summary>
    [JsonPropertyName("canUpdateOwnMetadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanUpdateOwnMetadata { get; init; }
    /// <summary>Hide the participant from others.</summary>
    [JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; init; }
}
