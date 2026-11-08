namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Hosting;

/// <summary>A session log in the list: its file and how it is named there.</summary>
/// <param name="File">The log file.</param>
/// <param name="IsCurrent">Whether it is this session's log.</param>
public sealed record LogFileItem(LogFileInfo File, bool IsCurrent)
{
    /// <summary>Start time and size, with this session marked.</summary>
    public string Title => $"{File.Started:yyyy-MM-dd HH:mm}{(IsCurrent ? " (now)" : "")} · {File.Size / 1024 + 1} KiB";
}

/// <summary>The app's session logs, as the terminal client's log window shows them: the sessions, and the entries of the chosen one at a level and above, newest first, filtered by text.</summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    /// <summary>Most entries shown at once, the newest.</summary>
    private const int MaxEntries = 1000;

    private readonly string directory;
    private readonly string currentPath;

    /// <summary>List the logs in <paramref name="directory"/> and show <paramref name="currentPath"/>, this session's log.</summary>
    public LogsViewModel(string directory, string currentPath)
    {
        // Sessions, then this one
        this.directory = directory;
        this.currentPath = currentPath;
        Refresh();
    }

    /// <summary>Levels to choose from, the least important first.</summary>
    public IReadOnlyList<string> Levels { get; } = ["Debug", "Information", "Warning", "Error"];

    /// <summary>The session logs, newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LogFileItem> Files { get; set; } = [];

    /// <summary>The chosen session.</summary>
    [ObservableProperty]
    public partial LogFileItem? Selected { get; set; }

    /// <summary>Index in <see cref="Levels"/> of the least important level shown; Information by default.</summary>
    [ObservableProperty]
    public partial int LevelIndex { get; set; } = 1;

    /// <summary>Text that shown entries contain, ignoring case; empty for all.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    /// <summary>The shown entries, newest first, one per paragraph.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Show the chosen session.</summary>
    partial void OnSelectedChanged(LogFileItem? value) => Show();

    /// <summary>Show again at the new level.</summary>
    partial void OnLevelIndexChanged(int value) => Show();

    /// <summary>Show again with the new filter.</summary>
    partial void OnFilterChanged(string value) => Show();

    /// <summary>List the sessions again and read the chosen one again, e.g. for what this session wrote since.</summary>
    [RelayCommand]
    private void Refresh()
    {
        // Keep the choice when it is still there
        string? chosen = Selected?.File.Path ?? currentPath;
        Files = [.. LogFiles.List(directory).Select(f => new LogFileItem(f, f.Path == currentPath))];
        LogFileItem? again = Files.FirstOrDefault(f => f.File.Path == chosen) ?? Files.FirstOrDefault();
        if (again == Selected) Show();
        else Selected = again;
    }

    /// <summary>Fill <see cref="Text"/> from the chosen session's file.</summary>
    private void Show()
    {
        // Assert a file that can be read
        if (Selected is not LogFileItem item)
        {
            Text = "";
            return;
        }
        IReadOnlyList<LogEntry> entries;
        try
        {
            entries = LogFiles.Read(item.File.Path);
        }
        catch (IOException exception)
        {
            Text = $"The log cannot be read: {exception.Message}";
            return;
        }

        // Matching entries, newest first
        LogLevel minimum = LevelIndex switch { 0 => LogLevel.Debug, 2 => LogLevel.Warning, 3 => LogLevel.Error, _ => LogLevel.Information };
        string? filter = Filter.Trim() is { Length: > 0 } text ? text : null;
        List<LogEntry> shown = [.. entries.Where(e => e.Matches(minimum, filter)).Reverse().Take(MaxEntries)];
        Text = shown.Count == 0 ? "No entries at this level." : string.Join("\n\n", shown.Select(e => string.Join("\n", e.Lines)));
    }
}
