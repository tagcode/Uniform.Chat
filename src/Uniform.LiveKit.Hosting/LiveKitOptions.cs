namespace Uniform.LiveKit.Hosting;

using Uniform.LiveKit.Authorization;
using Uniform.Matrix.Hosting;

/// <summary>Settings of the MatrixRTC authorization service in section <c>LiveKit</c>, e.g. <c>--LiveKit:Enabled=true --LiveKit:Url=wss://livekit.example.org</c>; changes apply at the next request.</summary>
public sealed class LiveKitOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "LiveKit";

    /// <summary>Whether to serve the authorization service.</summary>
    public bool Enabled { get; set; }

    /// <summary>LiveKit's WebSocket URL that clients join, e.g. <c>wss://livekit.example.org</c>.</summary>
    public string? Url { get; set; }

    /// <summary>The API key of LiveKit, a key of its <c>keys</c> setting.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The API secret of the key.</summary>
    [Secret]
    public string? ApiSecret { get; set; }

    /// <summary>Homeservers whose users may start calls and publish, separated by commas, or <c>*</c> for every homeserver; users of others only join calls that exist. The homeserver's own name when embedded.</summary>
    public string? FullAccessHomeservers { get; set; }

    /// <summary>Lifetime of the issued tokens, which only limits joining.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Whether participants may change their own name and metadata.</summary>
    public bool CanUpdateOwnMetadata { get; set; }

    /// <summary>Return what the authorizer needs.</summary>
    public LiveKitAuthorizerOptions ToAuthorizer() => new()
    {
        Url = Url ?? "",
        ApiKey = ApiKey ?? "",
        ApiSecret = ApiSecret ?? "",
        FullAccessHomeservers = (FullAccessHomeservers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        TokenLifetime = TokenLifetime,
        CanUpdateOwnMetadata = CanUpdateOwnMetadata,
    };
}
