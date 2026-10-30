namespace Uniform.Matrix.Client.Terminal;

using System.Runtime.InteropServices;
using System.Text;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Hosting;

/// <summary>Command-line help, shown for <c>--help</c>, and instead of starting when the user ID is missing or invalid and the console is not interactive.</summary>
internal static class Usage
{
    /// <summary>Get whether the console is interactive, so the user can be asked in the window instead of shown this help.</summary>
    public static bool CanAsk => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>Wait for Enter when the program has a console window of its own, as after a double-click on Windows, so the help can be read before it closes.</summary>
    public static void WaitIfOwnConsole()
    {
        // Only this process on the console: it was opened for us and closes when we exit
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || GetConsoleProcessList(new uint[2], 2) != 1) return;
        Console.Error.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    /// <summary>Command-line arguments that ask for this help.</summary>
    private static readonly string[] HelpArguments = ["--help", "-h", "-?", "/?", "help"];

    /// <summary>Return whether <paramref name="args"/> ask for help.</summary>
    public static bool IsHelp(string[] args) => args.Any(a => HelpArguments.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>Return the help text: why the client did not start when <paramref name="userId"/> is wrong, how to start it, and every setting with its default.</summary>
    /// <param name="userId">The configured user ID when it is the reason for the help, or null when help was asked for.</param>
    /// <param name="askedForHelp">Whether the user asked for help, so no problem is named.</param>
    /// <param name="settingsPath">The user's settings file.</param>
    /// <param name="options">The settings to list, in order.</param>
    public static string Text(string? userId, bool askedForHelp, string settingsPath, IReadOnlyList<OptionInfo> options)
    {
        // Program name as the user typed it, and why help is shown
        string program = Path.GetFileName(Environment.ProcessPath) ?? "Uniform.Matrix";
        string section = MatrixOptions.Section;
        string problem = askedForHelp ? "" : string.IsNullOrWhiteSpace(userId) ? "Give your Matrix user ID to start.\n\n" : $"\"{userId}\" is not a Matrix user ID; it looks like @alice:matrix.org.\n\n";

        // The settings: name, default, then the description wrapped under it
        StringBuilder list = new();
        foreach (OptionInfo option in Order(options))
        {
            string value = option.Secret ? "" : option.DefaultValue is { Length: > 0 } fallback ? $" (default {fallback})" : "";
            list.Append("  --").Append(option.Key).Append('=').Append(Placeholder(option)).AppendLine(value);
            foreach (string line in Wrap(Example(option) ?? option.Description, 72)) list.Append("      ").AppendLine(line);
        }
        // Return help
        return $$"""
            Uniform.Matrix, a Matrix chat client for the terminal

            {{problem}}Usage:
              {{program}} --{{section}}:UserId=@alice:matrix.org [--{{section}}:Password=...] [--Section:Setting=value ...]

            Settings, also read from {{settingsPath}},
            from appsettings.json next to the program, and from environment variables such as {{section}}__UserId:
            {{list.ToString().TrimEnd()}}

            Save the user ID in the settings file to start without options:
              { "{{section}}": { "UserId": "@alice:matrix.org" } }
            """;
    }

    /// <summary>Return the options with the account ones first, as they are what people need at the start.</summary>
    private static IEnumerable<OptionInfo> Order(IReadOnlyList<OptionInfo> options)
    {
        // User ID, password, homeserver, then the rest as registered
        string[] first = [$"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}", $"{MatrixOptions.Section}:{nameof(MatrixOptions.Password)}", $"{MatrixOptions.Section}:{nameof(MatrixOptions.Homeserver)}"];
        // Return ordered
        return options.OrderBy(o => Array.IndexOf(first, o.Key) is int i and >= 0 ? i : first.Length);
    }

    /// <summary>Return the description of the account options in words for people starting the client, or null for the others.</summary>
    private static string? Example(OptionInfo option) => option.Key switch
    {
        "Matrix:UserId" => "Your full Matrix ID, e.g. @alice:matrix.org. Needed at every start; save it in the settings file.",
        "Matrix:Password" => "Password for the first login; asked for when not given. Not needed once you are logged in.",
        "Matrix:Homeserver" => "Server address, when it cannot be found from the user ID, e.g. http://localhost:8008 for a server on this computer.",
        _ => null,
    };

    /// <summary>Return a placeholder of an option's value: its choices, or its type.</summary>
    private static string Placeholder(OptionInfo option)
    {
        // Choices, else the kind of value
        Type type = Nullable.GetUnderlyingType(option.Type) ?? option.Type;
        if (option.Key == "Matrix:UserId") return "@alice:matrix.org";
        if (option.Choices.Count > 0) return string.Join("|", option.Choices);
        // Return placeholder
        return type == typeof(TimeSpan) ? "hh:mm:ss" : type == typeof(string) ? "text" : type.Name.ToLowerInvariant() switch { "int32" or "int64" => "number", var name => name };
    }

    /// <summary>Return <paramref name="text"/> in lines of at most <paramref name="width"/> characters, broken between words.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        // Greedy, word by word
        StringBuilder line = new();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        // Last line
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>Fill <paramref name="processList"/> with the processes attached to the console; return their count.</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}
