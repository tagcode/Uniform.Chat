namespace Uniform.Matrix.Client.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>Account and sync settings, configuration section <see cref="Section"/>.</summary>
/// <example><code><![CDATA[dotnet run --project src/Uniform.Chat.Terminal -- --Matrix:UserId=@alice:matrix.org]]></code></example>
public sealed record MatrixOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Matrix";

    /// <summary>Full user ID, e.g. <c>@alice:matrix.org</c>; required, as it selects the local database.</summary>
    [RestartRequired]
    public string? UserId { get; set; }
    /// <summary>Homeserver address, e.g. <c>http://localhost:8008</c>, for servers without <c>.well-known</c> discovery; when unset, found from the server name of <see cref="UserId"/>. Used at the first login.</summary>
    [RestartRequired]
    public string? Homeserver { get; set; }
    /// <summary>Password for the first login; prompted when null. Not needed once a session is stored.</summary>
    [Secret]
    public string? Password { get; set; }
    /// <summary>Display name of the device created at login.</summary>
    [RestartRequired]
    public string DeviceName { get; set; } = "Uniform Chat Terminal";
    /// <summary>Long-poll time of each sync request.</summary>
    [RestartRequired]
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for the server to answer a request, at least <see cref="SyncTimeout"/> and 30 seconds; joining a large room on another server can take minutes.</summary>
    [RestartRequired]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Timeline events per room in each sync.</summary>
    [RestartRequired]
    public int TimelineLimit { get; set; } = 50;
    /// <summary>Wait before retrying unsent messages after a transient failure.</summary>
    [RestartRequired]
    public TimeSpan OutboxRetryInterval { get; set; } = TimeSpan.FromSeconds(15);
}
