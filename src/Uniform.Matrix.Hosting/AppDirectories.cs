namespace Uniform.Matrix.Hosting;

using System.Text;

/// <summary>Per-user directories of an application, following the <see href="https://specifications.freedesktop.org/basedir-spec/latest/">XDG base directories</see> on every platform.</summary>
/// <example><code><![CDATA[string data = AppDirectories.Data("Uniform.Matrix.Client.Terminal"); // ~/.local/share/uniform-matrix-client-terminal]]></code></example>
public static class AppDirectories
{
    /// <summary>Return the directory name of an application: its name in lowercase with dashes, e.g. <c>uniform-matrix-client-terminal</c>.</summary>
    public static string Name(string applicationName)
    {
        // Lowercase, words joined by dashes
        StringBuilder name = new();
        foreach (char c in applicationName)
        {
            if (char.IsLetterOrDigit(c)) name.Append(char.ToLowerInvariant(c));
            else if (name.Length > 0 && name[^1] != '-') name.Append('-');
        }
        // Return name without a trailing dash
        return name.ToString().TrimEnd('-');
    }

    /// <summary>Data directory: <c>$XDG_DATA_HOME/&lt;name&gt;</c> or <c>~/.local/share/&lt;name&gt;</c>.</summary>
    public static string Data(string applicationName) => Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), Name(applicationName));

    /// <summary>Configuration directory: <c>$XDG_CONFIG_HOME/&lt;name&gt;</c> or <c>~/.config/&lt;name&gt;</c>.</summary>
    public static string Config(string applicationName) => Path.Combine(Base("XDG_CONFIG_HOME", ".config"), Name(applicationName));

    /// <summary>Move the data and configuration directories of an application's previous name to its current name, once: only where the old directory exists and the new one does not yet.</summary>
    /// <param name="previousApplicationName">Name the application had, e.g. <c>Durum.Matrix.Server.Host</c>.</param>
    /// <param name="applicationName">Its name now, e.g. <c>Uniform.Matrix.Server.Host</c>.</param>
    /// <returns>Each move done, as <c>old → new</c>; empty when there was nothing to move.</returns>
    public static IReadOnlyList<string> MoveFromPreviousName(string previousApplicationName, string applicationName) =>
        MoveFromPreviousName(previousApplicationName, applicationName, Base("XDG_DATA_HOME", Path.Combine(".local", "share")), Base("XDG_CONFIG_HOME", ".config"));

    /// <summary>Move the directories of <paramref name="previousApplicationName"/> under <paramref name="dataRoot"/> and <paramref name="configRoot"/>; see <see cref="MoveFromPreviousName(string, string)"/>.</summary>
    internal static IReadOnlyList<string> MoveFromPreviousName(string previousApplicationName, string applicationName, string dataRoot, string configRoot)
    {
        // Data and configuration, each on its own
        List<string> moved = [];
        foreach (string root in new[] { dataRoot, configRoot }.Distinct())
        {
            string from = Path.Combine(root, Name(previousApplicationName));
            string to = Path.Combine(root, Name(applicationName));
            if (!Directory.Exists(from) || Directory.Exists(to)) continue;
            Directory.Move(from, to);
            moved.Add($"{from} → {to}");
        }
        // Return moves
        return moved;
    }

    /// <summary>Return the XDG variable's directory, or its default under the home directory.</summary>
    private static string Base(string variable, string fallback)
    {
        // Variable wins when set
        string? value = Environment.GetEnvironmentVariable(variable);
        // Return directory
        return string.IsNullOrEmpty(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback) : value;
    }
}
