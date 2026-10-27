namespace Uniform.Matrix.Server.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>Voice and video calls, configuration section <see cref="Section"/>: the <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4143">MatrixRTC</see> service of Element Call, TURN for older 1:1 calls, and delayed events.</summary>
public sealed record CallOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Calls";

    /// <summary>URL of the MatrixRTC authorization service (lk-jwt-service) in front of a LiveKit SFU, e.g. <c>https://example.org/livekit/jwt</c>; announced to clients, none when unset.</summary>
    public string? LiveKitServiceUrl { get; set; }
    /// <summary>TURN server URIs for older 1:1 calls, separated by commas, e.g. <c>turn:turn.example.org:3478?transport=udp</c>.</summary>
    public string? TurnUris { get; set; }
    /// <summary>Shared secret of the TURN server, e.g. coturn's <c>static-auth-secret</c>.</summary>
    [Secret]
    public string? TurnSharedSecret { get; set; }
    /// <summary>How long TURN credentials hold.</summary>
    public TimeSpan TurnUserLifetime { get; set; } = TimeSpan.FromDays(1);
    /// <summary>Longest delay of a delayed event.</summary>
    public TimeSpan MaxEventDelay { get; set; } = TimeSpan.FromDays(1);
    /// <summary>How long an OpenID token, which proves a user's identity to services such as the MatrixRTC authorization service, holds.</summary>
    public TimeSpan OpenIdTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Return <see cref="TurnUris"/> as a list.</summary>
    public IReadOnlyList<string> GetTurnUris() => (TurnUris ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
