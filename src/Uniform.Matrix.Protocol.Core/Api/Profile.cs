namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3profileuserid">GET /_matrix/client/v3/profile/{userId}</see>.</summary>
public sealed record ProfileResponse
{
    /// <summary>Global display name.</summary>
    [JsonPropertyName("displayname")]
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
}
