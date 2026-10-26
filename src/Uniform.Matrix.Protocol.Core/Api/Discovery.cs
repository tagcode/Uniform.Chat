namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientversions">GET /_matrix/client/versions</see>.</summary>
public sealed record VersionsResponse
{
    /// <summary>Supported spec versions, e.g. <c>v1.12</c>.</summary>
    public required IReadOnlyList<string> Versions { get; init; }
    /// <summary>Unstable features and whether they are enabled.</summary>
    public IReadOnlyDictionary<string, bool>? UnstableFeatures { get; init; }
}

/// <summary>Body of <see href="https://spec.matrix.org/latest/client-server-api/#getwell-knownmatrixclient">GET /.well-known/matrix/client</see>.</summary>
public sealed record WellKnownClient
{
    /// <summary>The <c>m.homeserver</c> entry.</summary>
    [JsonPropertyName("m.homeserver")]
    public WellKnownServer? Homeserver { get; init; }
}

/// <summary>The <c>m.homeserver</c> entry of <see cref="WellKnownClient"/>.</summary>
public sealed record WellKnownServer
{
    /// <summary>Homeserver base URL.</summary>
    public string? BaseUrl { get; init; }
}
