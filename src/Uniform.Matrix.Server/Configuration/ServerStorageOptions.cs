namespace Uniform.Matrix.Server.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>Local storage of the homeserver, configuration section <see cref="Section"/>.</summary>
public sealed record ServerStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Storage";

    /// <summary>Root directory; when unset, the application's data directory, e.g. <c>~/.local/share/uniform-matrix-server-host</c>, see <see cref="AppDirectories.Data"/>.</summary>
    [RestartRequired]
    public string? DataDirectory { get; set; }
    /// <summary>SQLite database file name inside <see cref="DataDirectory"/>.</summary>
    [RestartRequired]
    public string DatabaseFileName { get; set; } = "server.db";
    /// <summary>Media repository directory name inside <see cref="DataDirectory"/>.</summary>
    [RestartRequired]
    public string MediaDirectoryName { get; set; } = "media";
    /// <summary>Directory inside <see cref="DataDirectory"/> with one log file per session.</summary>
    [RestartRequired]
    public string LogDirectoryName { get; set; } = "logs";
    /// <summary>Session log files to keep; older ones are deleted at start.</summary>
    [RestartRequired]
    public int LogFilesKept { get; set; } = 20;

    /// <summary>Return <see cref="DataDirectory"/>, or the data directory of <paramref name="applicationName"/> when unset.</summary>
    public string ResolveDataDirectory(string applicationName) => DataDirectory ?? AppDirectories.Data(applicationName);

    /// <summary>Return the log directory under <see cref="ResolveDataDirectory"/>.</summary>
    public string ResolveLogDirectory(string applicationName) => Path.Combine(ResolveDataDirectory(applicationName), LogDirectoryName);
}
