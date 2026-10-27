namespace Uniform.Matrix.Client.Terminal;

using System.Runtime.InteropServices;
using Uniform.Matrix.Client.Configuration;

/// <summary>Command-line help, shown instead of starting when the user ID is missing or invalid.</summary>
internal static class Usage
{
    /// <summary>Wait for Enter when the program has a console window of its own, as after a double-click on Windows, so the help can be read before it closes.</summary>
    public static void WaitIfOwnConsole()
    {
        // Only this process on the console: it was opened for us and closes when we exit
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || GetConsoleProcessList(new uint[2], 2) != 1) return;
        Console.Error.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    /// <summary>Return the help text: why the client did not start, the options, and where settings are saved.</summary>
    /// <param name="userId">The configured user ID, or null when none is set.</param>
    /// <param name="settingsPath">The user's settings file.</param>
    public static string Text(string? userId, string settingsPath)
    {
        // Program name as the user typed it
        string program = Path.GetFileName(Environment.ProcessPath) ?? "Uniform.Matrix.Client.Terminal";
        string problem = string.IsNullOrWhiteSpace(userId) ? "Give your Matrix user ID to start." : $"\"{userId}\" is not a Matrix user ID; it looks like @alice:matrix.org.";
        string section = MatrixOptions.Section;
        // Return help
        return $$"""
            Uniform.Matrix, a Matrix chat client for the terminal

            {{problem}}

            Usage:
              {{program}} --{{section}}:UserId=@alice:matrix.org [--{{section}}:Password=...] [--{{section}}:Homeserver=https://matrix.example.org]

            Options:
              --{{section}}:UserId      Your full Matrix ID, needed at every start; save it in settings below.
              --{{section}}:Password    Password for the first login. Asked for when not given.
              --{{section}}:Homeserver  Server address, when it cannot be found from the user ID,
                                   e.g. http://localhost:8008 for a server on this computer.

            To start without options, save them in {{settingsPath}}:
              { "{{section}}": { "UserId": "@alice:matrix.org" } }
            """;
    }

    /// <summary>Fill <paramref name="processList"/> with the processes attached to the console; return their count.</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}
