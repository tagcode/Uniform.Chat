namespace Uniform.Matrix.Server.Host.Ui;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using global::Terminal.Gui.App;
using global::Terminal.Gui.Input;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Server dashboard: identity, addresses and live counters, the user and room admin windows, and the shared settings and log windows.</summary>
public sealed class ServerWindow : Window
{
    private readonly IApplication app;
    private readonly ServerStatus status;
    private readonly IServer server;
    private readonly SettingsStore settings;
    private readonly OptionCatalog catalog;
    private readonly IConfiguration configuration;
    private readonly FileLoggerProvider fileLogger;
    private readonly ServerStorageOptions storage;
    private readonly IHostEnvironment environment;
    private readonly TimeProvider time;
    private readonly AdminService admin;
    private readonly Label summary = new() { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(2) };
    private object? timer;
    private int ticks;
    private (int Users, int Rooms)? counts;

    /// <summary>Build the dashboard over the running server's services.</summary>
    public ServerWindow(IApplication app, ServerStatus status, IServer server, SettingsStore settings, OptionCatalog catalog, IConfiguration configuration, FileLoggerProvider fileLogger, IOptions<ServerStorageOptions> storage, IOptions<ServerOptions> options, IHostEnvironment environment, TimeProvider time, AdminService admin)
    {
        // Dependencies
        this.app = app;
        this.status = status;
        this.server = server;
        this.settings = settings;
        this.catalog = catalog;
        this.configuration = configuration;
        this.fileLogger = fileLogger;
        this.storage = storage.Value;
        this.environment = environment;
        this.time = time;
        this.admin = admin;

        // Menu and dashboard
        Title = $"Matrix server {options.Value.ServerName}";
        MenuBar menu = new([
            new MenuBarItem("_File", [
                new MenuItem { Title = "_Settings…", HelpText = "View and change options", Action = ShowSettings },
                new MenuItem { Title = "_Logs…", HelpText = "This and earlier sessions", Action = ShowLogs },
                new Line(),
                new MenuItem { Title = "_Quit", HelpText = "Stop the server", Key = Key.Q.WithCtrl, Action = () => app.RequestStop() },
            ]),
            new MenuBarItem("_Server", [
                new MenuItem { Title = "_Users…", HelpText = "Create users, reset passwords, admins", Action = ShowUsers },
                new MenuItem { Title = "_Rooms…", HelpText = "Rooms and the public directory", Action = ShowRooms },
            ]),
        ]);
        Add(menu, summary);
        app.Keyboard.KeyDown += (_, key) => { if (key == Key.Q.WithCtrl && ReferenceEquals(app.TopRunnableView, this)) { key.Handled = true; app.RequestStop(); } };

        // Refresh every second
        Refresh();
        timer = app.AddTimeout(TimeSpan.FromSeconds(1), () => { Refresh(); return true; });
    }

    /// <summary>Stop refreshing.</summary>
    protected override void Dispose(bool disposing)
    {
        // Remove the timer before the views go
        if (disposing && timer is not null) app.RemoveTimeout(timer);
        timer = null;
        base.Dispose(disposing);
    }

    /// <summary>Show addresses and counters.</summary>
    private void Refresh()
    {
        // Listening addresses come from Kestrel once started
        IEnumerable<string> addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        TimeSpan uptime = time.GetUtcNow() - status.Started;
        if (ticks++ % 5 == 0) _ = CountAsync();
        summary.Text = string.Join('\n',
            $"Listening:  {string.Join(", ", addresses)}",
            $"Uptime:     {(int)uptime.TotalHours}:{uptime:mm\\:ss}",
            $"Requests:   {status.Requests} answered, {status.ActiveRequests} in progress, {status.FailedRequests} failed",
            counts is (int users, int rooms) ? $"Accounts:   {users} users, {rooms} rooms" : "Accounts:   counting…",
            "",
            $"Data:       {storage.ResolveDataDirectory(environment.ApplicationName)}",
            $"Log:        {fileLogger.FilePath}",
            "",
            "F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q stops the server.");
    }

    /// <summary>Count users and rooms in the background for the dashboard.</summary>
    private async Task CountAsync()
    {
        // Off the UI thread; a failure keeps the old counts
        try
        {
            IReadOnlyList<AdminUser> users = await Task.Run(() => admin.GetUsersAsync(CancellationToken.None));
            IReadOnlyList<AdminRoom> rooms = await Task.Run(() => admin.GetRoomsAsync(CancellationToken.None));
            counts = (users.Count, rooms.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the old counts; the log window shows database errors
        }
    }

    /// <summary>Open the users window.</summary>
    private void ShowUsers()
    {
        // Until closed
        using UsersWindow window = new(app, admin);
        app.Run(window);
        _ = CountAsync();
    }

    /// <summary>Open the rooms window.</summary>
    private void ShowRooms()
    {
        // Until closed
        using RoomsWindow window = new(app, admin);
        app.Run(window);
        _ = CountAsync();
    }

    /// <summary>Open the shared settings window.</summary>
    private void ShowSettings()
    {
        // Run until closed with nothing unsaved, or confirmed
        using SettingsWindow window = new(app, new SettingsEditor(settings, catalog.GetAll(configuration)));
        do app.Run(window);
        while (!window.ConfirmClose());
    }

    /// <summary>Open the shared log window on this session's log.</summary>
    private void ShowLogs()
    {
        // Session logs of the server
        using LogWindow window = new(app, storage.ResolveLogDirectory(environment.ApplicationName), fileLogger.FilePath, legacyPath: null);
        app.Run(window);
    }
}
