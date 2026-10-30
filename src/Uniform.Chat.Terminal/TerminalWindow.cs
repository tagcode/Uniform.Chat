namespace Uniform.Chat.Terminal;

using System.Diagnostics;

/// <summary>Open the client in a terminal window when it was started without one, e.g. by a double-click in a Linux file manager, which runs it with no terminal to draw in.</summary>
internal static class TerminalWindow
{
    /// <summary>Environment variable set for the reopened client, so it never opens yet another window.</summary>
    private const string ReopenedVariable = "UNIFORM_MATRIX_IN_TERMINAL";

    /// <summary>Terminal emulators to try, with the argument that comes before the command; <c>x-terminal-emulator</c> is the desktop's choice on Debian, Ubuntu and Mint.</summary>
    private static readonly (string Program, string[] Before)[] Emulators =
    [
        ("x-terminal-emulator", ["-e"]),
        ("gnome-terminal", ["--"]),
        ("konsole", ["-e"]),
        ("xfce4-terminal", ["-x"]),
        ("mate-terminal", ["-x"]),
        ("tilix", ["-e"]),
        ("kitty", []),
        ("alacritty", ["-e"]),
        ("foot", []),
        ("xterm", ["-e"]),
    ];

    /// <summary>Start this program with <paramref name="args"/> in a terminal window of the desktop session; return false on other systems, outside a desktop session, when already reopened, or when no terminal emulator is found.</summary>
    public static bool TryReopen(string[] args)
    {
        // Assert a Linux desktop session, and not reopened already
        if (!OperatingSystem.IsLinux() || Environment.ProcessPath is not string self) return false;
        if (Environment.GetEnvironmentVariable(ReopenedVariable) is not null) return false;
        if (Environment.GetEnvironmentVariable("DISPLAY") is null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) return false;

        // The user's own choice first, then the known ones
        IEnumerable<(string Program, string[] Before)> candidates = Emulators;
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } chosen) candidates = candidates.Prepend((chosen, ["-e"]));
        foreach ((string program, string[] before) in candidates)
        {
            if (Find(program) is not string path) continue;
            ProcessStartInfo start = new(path) { UseShellExecute = false };
            foreach (string argument in before.Append(self).Concat(args)) start.ArgumentList.Add(argument);
            start.Environment[ReopenedVariable] = "1";
            try
            {
                using Process? started = Process.Start(start);
                if (started is not null) return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not runnable after all; try the next
            }
        }
        // On no terminal emulator
        return false;
    }

    /// <summary>Return the full path of <paramref name="program"/>, as given when it has a directory, else from PATH; null when not found.</summary>
    private static string? Find(string program)
    {
        // A path as is, else each PATH directory
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        // Return found
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, program)).FirstOrDefault(File.Exists);
    }
}
