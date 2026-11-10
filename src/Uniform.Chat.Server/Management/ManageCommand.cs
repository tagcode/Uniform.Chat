namespace Uniform.Chat.Server.Management;

using System.Net.Sockets;
using Uniform.Chat.Server.Ui;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.App;

/// <summary><c>uniform-chat-server manage</c>: the dashboard over a server that already runs, without stopping it. Through the local admin socket of a service by default, with its settings and logs; or over the network to a server's address as an admin, with its users, rooms and reports.</summary>
/// <example><code><![CDATA[
/// sudo uniform-chat-server manage
/// uniform-chat-server manage --socket=/run/uniform-chat-server/admin.sock
/// uniform-chat-server manage https://matrix.example.org --user=@admin:example.org
/// ]]></code></example>
internal static class ManageCommand
{
    /// <summary>The command's name, the program's first argument.</summary>
    public const string Name = "manage";

    /// <summary>Run the manager with the arguments after the command's name; return the exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        // Arguments: an address, or the socket
        string? address = arguments.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        string socket = Option(arguments, "--socket") ?? ServerHostOptions.ServiceAdminSocket;
        string? user = Option(arguments, "--user");
        if (arguments.Contains("--help") || arguments.Contains("-h"))
        {
            Console.WriteLine("Manage a running server: sudo uniform-chat-server manage [--socket=PATH]");
            Console.WriteLine("Manage a server over the network as an admin: uniform-chat-server manage https://matrix.example.org [--user=@admin:example.org]");
            return 0;
        }

        // Connect, then show the dashboard; a network session is logged out at the end
        HttpServerAdmin? admin = address is null ? await ConnectSocketAsync(socket) : await LoginAsync(address, user);
        if (admin is null) return 1;
        await using (admin)
        {
            DashboardOptions dashboard = await DashboardOfAsync(admin);
            using IApplication ui = Application.Create();
            ui.Init();
            using ServerWindow window = new(ui, admin, dashboard, TimeProvider.System);
            ui.Run(window);
        }
        // Done
        return 0;
    }

    /// <summary>Return what the dashboard may do: through the socket also the settings, which apply when the service starts again.</summary>
    private static async Task<DashboardOptions> DashboardOfAsync(HttpServerAdmin admin)
    {
        // Network: the admin lists only
        const string hint = "F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q closes the manager; the server keeps running.";
        if (!admin.IsSocket) return new DashboardOptions { QuitHelp = "Close the manager", Hint = hint };

        // Socket: the settings file the server reports
        ServerSnapshot snapshot = await admin.GetSnapshotAsync(CancellationToken.None);
        // Return options
        return new DashboardOptions
        {
            OpenSettings = snapshot.SettingsFile is string path ? () => ManagedSettings.Open(path) : null,
            SettingsOtherProcess = "The server reads the file when it starts again, e.g. with: sudo systemctl restart uniform-chat-server",
            QuitHelp = "Close the manager",
            Hint = hint,
        };
    }

    /// <summary>Connect to the admin socket; tell why not and return null when it fails.</summary>
    private static async Task<HttpServerAdmin?> ConnectSocketAsync(string path)
    {
        // Running, and allowed
        try
        {
            return await HttpServerAdmin.ConnectSocketAsync(path, CancellationToken.None);
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Cannot open the server's admin socket {path}: {(ex.InnerException as SocketException)?.Message ?? ex.Message}");
            Console.Error.WriteLine("Is the server running, e.g. systemctl status uniform-chat-server? The socket needs root or the server's group: sudo uniform-chat-server manage");
            Console.Error.WriteLine("To manage a server elsewhere, give its address: uniform-chat-server manage https://matrix.example.org");
            // On failure
            return null;
        }
    }

    /// <summary>Ask for the user and password, and log in at <paramref name="address"/>; tell why not and return null when it fails.</summary>
    private static async Task<HttpServerAdmin?> LoginAsync(string address, string? user)
    {
        // Address, then credentials
        if (!Uri.TryCreate(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address, UriKind.Absolute, out Uri? homeserver))
        {
            Console.Error.WriteLine($"Not an address: {address}");
            return null;
        }
        user ??= Ask("Admin user, e.g. @admin:example.org: ");
        string password = AskSecret($"Password of {user}: ");

        // Log in as an admin
        try
        {
            return await HttpServerAdmin.LoginAsync(homeserver, user, password, CancellationToken.None);
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException)
        {
            Console.Error.WriteLine($"Cannot log in at {homeserver}: {(ex as MatrixException)?.Error ?? ex.Message}");
            // On failure
            return null;
        }
    }

    /// <summary>Return the value of <c>--name=value</c>, or null.</summary>
    private static string? Option(IReadOnlyList<string> arguments, string name) => arguments.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    /// <summary>Ask for a line.</summary>
    private static string Ask(string prompt)
    {
        // Echoed
        Console.Write(prompt);
        // Return answer
        return Console.ReadLine()?.Trim() ?? "";
    }

    /// <summary>Ask for a line without echoing it, e.g. a password.</summary>
    private static string AskSecret(string prompt)
    {
        // Redirected input has no keys to hide
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        // Key by key, with backspace
        System.Text.StringBuilder secret = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && secret.Length > 0) secret.Length--;
            else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
        }
        Console.WriteLine();
        // Return secret
        return secret.ToString();
    }
}
