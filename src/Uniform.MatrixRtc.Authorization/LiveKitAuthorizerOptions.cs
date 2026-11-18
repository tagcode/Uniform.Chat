namespace Uniform.LiveKit.Authorization;

/// <summary>What <see cref="LiveKitAuthorizer"/> needs: the LiveKit server, its API key, and whose users may start calls.</summary>
public sealed record LiveKitAuthorizerOptions
{
    /// <summary>LiveKit's WebSocket URL that clients join, e.g. <c>wss://livekit.example.org</c>.</summary>
    public string Url { get; init; } = "";
    /// <summary>The API key of LiveKit.</summary>
    public string ApiKey { get; init; } = "";
    /// <summary>The API secret of the key.</summary>
    public string ApiSecret { get; init; } = "";
    /// <summary>Homeservers whose users may create rooms and publish; <c>*</c> for every homeserver. Users of others only join calls that exist, as LiveKit does not create rooms on their join.</summary>
    public IReadOnlyList<string> FullAccessHomeservers { get; init; } = [];
    /// <summary>Lifetime of the issued tokens, which only limits joining: LiveKit keeps a joined participant.</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Whether participants may change their own name and metadata.</summary>
    public bool CanUpdateOwnMetadata { get; init; }

    /// <summary>Return whether users of <paramref name="serverName"/> have full access.</summary>
    public bool HasFullAccess(string serverName) => FullAccessHomeservers.Any(allowed => allowed == "*" || string.Equals(allowed, serverName, StringComparison.OrdinalIgnoreCase));
}
