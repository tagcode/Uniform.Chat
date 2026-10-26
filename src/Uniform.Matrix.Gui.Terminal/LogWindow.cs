namespace Uniform.Matrix.Gui.Terminal;

using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;
using Uniform.Matrix.Hosting;
using global::Terminal.Gui.App;
using global::Terminal.Gui.Drawing;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Log viewer: session logs on the left, the chosen log on the right, filtered by level and text and colored by level.</summary>
/// <remarks>The current session's log follows new lines while scrolled to the end. Enter shows a whole entry, e.g. with its stack trace.</remarks>
public sealed class LogWindow : Dialog
{
    private static readonly LogLevel[] Levels = [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];
    private readonly IApplication app;
    private readonly string directory;
    private readonly string? legacyPath;
    private readonly string currentPath;
    private readonly List<LogFileInfo> files = [];
    private readonly ObservableCollection<string> fileRows = [];
    private readonly ListView fileList = new() { Title = "Sessions", X = 0, Y = 0, Width = 30, Height = Dim.Fill(1), BorderStyle = LineStyle.Single };
    private readonly TextField filter = new() { Y = 0, Width = Dim.Fill(24) };
    private readonly Button levelButton = new() { Y = 0, X = Pos.AnchorEnd(22) };
    private readonly ObservableCollection<string> lines = [];
    private readonly List<LogEntry> lineEntries = [];
    private readonly ListView logView = new() { X = 30, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), BorderStyle = LineStyle.Single };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private IReadOnlyList<LogEntry> entries = [];
    private LogFileInfo? shown;
    private long shownSize = -1;
    private int levelIndex = 2;
    private object? followTimer;

    /// <summary>List logs in <paramref name="directory"/> and open <paramref name="currentPath"/>, this session's log.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="directory">Session log directory.</param>
    /// <param name="currentPath">This session's log file.</param>
    /// <param name="legacyPath">The single log of older versions, listed last when it exists.</param>
    public LogWindow(IApplication app, string directory, string currentPath, string? legacyPath)
    {
        // Layout: sessions | filter, level, log | status
        this.app = app;
        this.directory = directory;
        this.currentPath = currentPath;
        this.legacyPath = legacyPath;
        Title = "Logs";
        Width = Dim.Fill(1);
        Height = Dim.Fill(1);
        Label filterLabel = new() { Text = "_Filter:", X = 31, Y = 0 };
        filter.X = Pos.Right(filterLabel) + 1;
        fileList.SetSource(fileRows);
        logView.SetSource(lines);
        logView.RowRender += OnRowRender;
        Add(fileList, filterLabel, filter, levelButton, logView, status);
        AddButton(new Button { Text = "_Close" });

        // Interaction
        fileList.ValueChanged += (_, _) => ShowSelectedFile();
        filter.TextChanged += (_, _) => Rebuild(scrollToEnd: true);
        levelButton.Accepting += (_, e) => { e.Handled = true; levelIndex = (levelIndex + 1) % Levels.Length; Rebuild(scrollToEnd: true); };
        logView.Accepting += (_, e) => { e.Handled = true; ShowEntry(); };
        fileList.Accepting += (_, e) => { e.Handled = true; logView.SetFocus(); };

        // Start on the current session, following it
        LoadFiles();
        fileList.SelectedItem = Math.Max(0, files.FindIndex(f => f.Path == currentPath));
        ShowSelectedFile();
        followTimer = app.AddTimeout(TimeSpan.FromSeconds(1), Follow);
        logView.SetFocus();
    }

    /// <summary>Stop following the log.</summary>
    protected override void Dispose(bool disposing)
    {
        // Remove the timer before the views go
        if (disposing && followTimer is not null) app.RemoveTimeout(followTimer);
        followTimer = null;
        base.Dispose(disposing);
    }

    /// <summary>List the log files.</summary>
    private void LoadFiles()
    {
        // Newest first, current marked
        files.Clear();
        files.AddRange(LogFiles.List(directory, legacyPath));
        fileRows.Clear();
        foreach (LogFileInfo file in files)
        {
            string label = file.Legacy ? "older log" : file.Path == currentPath ? "now" : FormatSize(file.Size);
            fileRows.Add($"{file.Started:yyyy-MM-dd HH:mm}  {label}");
        }
    }

    /// <summary>Read and show the selected file.</summary>
    private void ShowSelectedFile()
    {
        // Assert selection
        if (fileList.SelectedItem is not int index || index >= files.Count) return;
        shown = files[index];
        Reload(scrollToEnd: true);
    }

    /// <summary>Read the shown file again.</summary>
    private void Reload(bool scrollToEnd)
    {
        // Read; the file may be gone or locked
        if (shown is null) return;
        try
        {
            entries = LogFiles.Read(shown.Path);
            shownSize = new FileInfo(shown.Path).Length;
        }
        catch (IOException ex)
        {
            entries = [new LogEntry { Lines = [$"Cannot read {shown.Path}: {ex.Message}"] }];
            shownSize = -1;
        }
        Rebuild(scrollToEnd);
    }

    /// <summary>Show the entries that pass the level and text filter, first line of each.</summary>
    private void Rebuild(bool scrollToEnd)
    {
        // Filter
        LogLevel minimum = Levels[levelIndex];
        string text = filter.Text.Trim();
        levelButton.Text = $"_Level: {minimum}+";
        lines.Clear();
        lineEntries.Clear();
        foreach (LogEntry entry in entries.Where(e => e.Matches(minimum, text)))
        {
            string more = entry.Lines.Count > 1 ? $"  [+{entry.Lines.Count - 1} lines]" : "";
            lines.Add(entry.Lines[0] + more);
            lineEntries.Add(entry);
        }

        // Scroll and count
        if (scrollToEnd && lines.Count > 0) logView.SelectedItem = lines.Count - 1;
        status.Text = $"{lineEntries.Count} of {entries.Count} entries · {shown?.Path}   Enter: whole entry  Alt+F: filter";
        logView.SetNeedsDraw();
    }

    /// <summary>Re-reads the current session's log when it grew, staying at the end when there.</summary>
    private bool Follow()
    {
        // Only the live file
        if (shown is null || shown.Path != currentPath) return true;
        long size;
        try
        {
            size = new FileInfo(shown.Path).Length;
        }
        catch (IOException)
        {
            return true;
        }
        if (size == shownSize) return true;

        // Reload, following when at the end
        bool atEnd = logView.SelectedItem is null || logView.SelectedItem >= lines.Count - 1;
        int? selected = logView.SelectedItem;
        Reload(atEnd);
        if (!atEnd) logView.SelectedItem = selected;
        // Keep the timer
        return true;
    }

    /// <summary>Show all lines of the selected entry.</summary>
    private void ShowEntry()
    {
        // Assert selection
        if (logView.SelectedItem is not int index || index >= lineEntries.Count) return;
        LogEntry entry = lineEntries[index];
        Dialogs.ShowText(app, $"{entry.Level} {entry.Time}", string.Join('\n', entry.Lines));
    }

    /// <summary>Color lines by level.</summary>
    private void OnRowRender(object? sender, ListViewRowEventArgs args)
    {
        // Selected row keeps the focus color
        if (args.Row == logView.SelectedItem && logView.HasFocus) return;
        TuiAttribute normal = logView.GetAttributeForRole(VisualRole.Normal);
        LogLevel? level = args.Row < lineEntries.Count ? lineEntries[args.Row].Level : null;
        Color foreground = level switch
        {
            LogLevel.Trace or LogLevel.Debug => Color.DarkGray,
            LogLevel.Warning => Color.BrightYellow,
            LogLevel.Error => Color.BrightRed,
            LogLevel.Critical => Color.Red,
            _ => normal.Foreground,
        };
        // Apply
        args.RowAttribute = new TuiAttribute(foreground, normal.Background);
    }

    /// <summary>Format a size as B, KB or MB.</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024.0):0.0} MB",
    };
}
