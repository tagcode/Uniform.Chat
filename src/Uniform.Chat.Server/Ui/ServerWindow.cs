namespace Uniform.Chat.Server.Ui;

using Uniform.Chat.Server.Management;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Rooms;
using global::Terminal.Gui.App;
using global::Terminal.Gui.Input;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>What a dashboard may do besides the admin lists, which depends on how it reaches the server.</summary>
public sealed record DashboardOptions
{
    /// <summary>Open the server's settings; null when this dashboard cannot reach them, as over the network.</summary>
    public Func<SettingsEditor>? OpenSettings { get; init; }
    /// <summary>What quitting does, shown in the menu.</summary>
    public string QuitHelp { get; init; } = "Stop the server";
    /// <summary>The last line of the dashboard: how to use it.</summary>
    public string Hint { get; init; } = "F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q stops the server.";
}

/// <summary>Server dashboard: identity, addresses and live counters, the user, room and report windows, and the shared settings and log windows where the server's files can be reached. It runs in the server's own process, or in a manager attached to a running server.</summary>
public sealed class ServerWindow : Window
{
    private readonly IApplication app;
    private readonly IServerAdmin admin;
    private readonly DashboardOptions dashboard;
    private readonly TimeProvider time;
    private readonly Label summary = new() { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(2) };
    private object? timer;
    private int ticks;
    private int refreshing;
    private ServerSnapshot? snapshot;
    private string? error;
    private (int Users, int Rooms, int Reports)? counts;

    /// <summary>Build the dashboard over <paramref name="admin"/>.</summary>
    /// <param name="app">Application the window runs in.</param>
    /// <param name="admin">The server, in this process or attached to.</param>
    /// <param name="dashboard">What this dashboard may do besides the admin lists.</param>
    /// <param name="time">Clock of the uptime.</param>
    public ServerWindow(IApplication app, IServerAdmin admin, DashboardOptions dashboard, TimeProvider time)
    {
        // Dependencies
        this.app = app;
        this.admin = admin;
        this.dashboard = dashboard;
        this.time = time;

        // Menu and dashboard; settings and logs only where they can be reached
        Title = "Matrix server";
        List<View> file = [];
        if (dashboard.OpenSettings is not null) file.Add(new MenuItem { Title = "_Settings…", HelpText = "View and change options", Action = ShowSettings });
        file.Add(new MenuItem { Title = "_Logs…", HelpText = "This and earlier sessions", Action = ShowLogs });
        file.Add(new Line());
        file.Add(new MenuItem { Title = "_Quit", HelpText = dashboard.QuitHelp, Key = Key.Q.WithCtrl, Action = () => app.RequestStop() });
        MenuBar menu = new([
            new MenuBarItem("_File", [.. file]),
            new MenuBarItem("_Server", [
                new MenuItem { Title = "_Users…", HelpText = "Create users, reset passwords, admins", Action = ShowUsers },
                new MenuItem { Title = "_Rooms…", HelpText = "Rooms and the public directory", Action = ShowRooms },
                new MenuItem { Title = "Re_ports…", HelpText = "Review what users reported", Action = ShowReports },
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

    /// <summary>Show the last snapshot and counts, and fetch new ones in the background: the snapshot each second, the counts every five.</summary>
    private void Refresh()
    {
        // Fetch, one at a time
        if (Interlocked.Exchange(ref refreshing, 1) == 0) _ = FetchAsync(ticks++ % 5 == 0);
        if (snapshot is not ServerSnapshot server)
        {
            summary.Text = error is null ? "Connecting…" : $"The server does not answer: {error}";
            return;
        }

        // What the snapshot tells
        Title = $"Matrix server {server.ServerName}";
        List<string> lines = [$"Listening:  {string.Join(", ", server.Addresses)}"];
        if (server.Started is DateTimeOffset started)
        {
            TimeSpan uptime = time.GetUtcNow() - started;
            lines.Add($"Uptime:     {(int)uptime.TotalHours}:{uptime:mm\\:ss}");
        }
        if (server.Requests is long requests) lines.Add($"Requests:   {requests} answered, {server.ActiveRequests} in progress, {server.FailedRequests} failed");
        lines.Add(counts is (int users, int rooms, _) ? $"Accounts:   {users} users, {rooms} rooms" : "Accounts:   counting…");
        lines.Add(counts is (_, _, int open) ? $"Reports:    {(open == 0 ? "none open" : $"{open} open; Server → Reports to review")}" : "Reports:    counting…");
        lines.Add("");
        if (server.DataDirectory is string data) lines.Add($"Data:       {data}");
        if (server.LogFile is string log) lines.Add($"Log:        {log}");
        if (server.SettingsFile is string settings) lines.Add($"Settings:   {settings}");
        if (error is not null) lines.Add($"Last error: {error}");
        lines.Add("");
        lines.Add(dashboard.Hint);
        // Done
        summary.Text = string.Join('\n', lines);
    }

    /// <summary>Fetch the snapshot, and the counts when <paramref name="count"/>; a failure keeps the old ones and shows the error.</summary>
    private async Task FetchAsync(bool count)
    {
        // Off the UI thread; the next refresh shows the result
        try
        {
            snapshot = await Task.Run(() => admin.GetSnapshotAsync(CancellationToken.None));
            if (count) await CountAsync();
            error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    /// <summary>Count users, rooms and open reports.</summary>
    private async Task CountAsync()
    {
        // Three lists
        IReadOnlyList<AdminUser> users = await Task.Run(() => admin.GetUsersAsync(CancellationToken.None));
        IReadOnlyList<AdminRoom> rooms = await Task.Run(() => admin.GetRoomsAsync(CancellationToken.None));
        IReadOnlyList<Report> open = await Task.Run(() => admin.GetReportsAsync(includeResolved: false, CancellationToken.None));
        // Done
        counts = (users.Count, rooms.Count, open.Count);
    }

    /// <summary>Open the users window.</summary>
    private void ShowUsers()
    {
        // Until closed, then count again
        using UsersWindow window = new(app, admin);
        app.Run(window);
        _ = FetchAsync(count: true);
    }

    /// <summary>Open the reports window.</summary>
    private void ShowReports()
    {
        // Until closed, then count again
        using ReportsWindow window = new(app, admin);
        app.Run(window);
        _ = FetchAsync(count: true);
    }

    /// <summary>Open the rooms window.</summary>
    private void ShowRooms()
    {
        // Until closed, then count again
        using RoomsWindow window = new(app, admin);
        app.Run(window);
        _ = FetchAsync(count: true);
    }

    /// <summary>Open the shared settings window.</summary>
    private void ShowSettings()
    {
        // The editor, which may fail to read another process's file
        SettingsEditor editor;
        try { editor = dashboard.OpenSettings!(); }
        catch (IOException ex)
        {
            Dialogs.Error(app, "Settings", ex.Message);
            return;
        }

        // Run until closed with nothing unsaved, or confirmed
        using SettingsWindow window = new(app, editor);
        do app.Run(window);
        while (!window.ConfirmClose());
    }

    /// <summary>Open the shared log window on the server's session logs, when this machine can read them.</summary>
    private void ShowLogs()
    {
        // Known and readable
        if (snapshot is not { LogDirectory: string directory, LogFile: string current })
        {
            Dialogs.Error(app, "Logs", "The server's logs are on its own machine. Manage it there, e.g. with sudo uniform-chat-server manage.");
            return;
        }
        try { _ = Directory.EnumerateFiles(directory).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(app, "Logs", $"{ex.Message}\n\nThe server's logs need its owner's rights, e.g. sudo.");
            return;
        }
        // Until closed
        using LogWindow window = new(app, directory, current, legacyPath: null);
        app.Run(window);
    }
}
