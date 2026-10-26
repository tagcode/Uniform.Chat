namespace Uniform.Matrix.Hosting;

using System.Text;

/// <summary>Per-user directories of an application, following the <see href="https://specifications.freedesktop.org/basedir-spec/latest/">XDG base directories</see> on every platform.</summary>
/// <example><code><![CDATA[string data = AppDirectories.Data("Durum.Matrix.Client.Terminal"); // ~/.local/share/durum-matrix-client-terminal]]></code></example>
public static class AppDirectories
{
    /// <summary>Return the directory name of an application: its name in lowercase with dashes, e.g. <c>durum-matrix-client-terminal</c>.</summary>
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

    /// <summary>Return the XDG variable's directory, or its default under the home directory.</summary>
    private static string Base(string variable, string fallback)
    {
        // Variable wins when set
        string? value = Environment.GetEnvironmentVariable(variable);
        // Return directory
        return string.IsNullOrEmpty(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback) : value;
    }
}
