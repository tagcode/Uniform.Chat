namespace Uniform.Matrix.Server.Configuration;

/// <summary>Sending <see href="https://spec.matrix.org/latest/client-server-api/#push-notifications">push notifications</see> to the push gateways of users' pushers, configuration section <see cref="Section"/>.</summary>
public sealed record PushOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Push";

    /// <summary>Whether to send to gateways at private, loopback and link-local addresses; off, so accounts cannot make the server reach machines inside its network.</summary>
    public bool AllowPrivateGateways { get; set; }
    /// <summary>How long to wait for a gateway to answer.</summary>
    public TimeSpan GatewayTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
