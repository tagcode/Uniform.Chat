namespace Uniform.Matrix.Hosting;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>A log file of one session, from <see cref="LogFiles.List"/>.</summary>
public sealed record LogFileInfo
{
    /// <summary>Full path.</summary>
    public required string Path { get; init; }
    /// <summary>Session start, from the file name; last write time for the single log of older versions.</summary>
    public required DateTime Started { get; init; }
    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }
    /// <summary>Whether this is the single <c>client.log</c> written before logs were split by session.</summary>
    public bool Legacy { get; init; }
}

/// <summary>One log entry: the first line with time, level and category, and following lines such as an exception's stack trace.</summary>
public sealed record LogEntry
{
    /// <summary>Time text as written, e.g. <c>2026-10-26 01:05:36.901</c>; null for lines before the first entry.</summary>
    public string? Time { get; init; }
    /// <summary>Level, or null when the first line is not in the log format.</summary>
    public LogLevel? Level { get; init; }
    /// <summary>Logger category, e.g. a class name.</summary>
    public string? Category { get; init; }
    /// <summary>All lines of the entry.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Return whether the entry is at least <paramref name="minimum"/> and contains <paramref name="filter"/>, ignoring case.</summary>
    public bool Matches(LogLevel minimum, string? filter) =>
        (Level ?? LogLevel.Information) >= minimum && (string.IsNullOrEmpty(filter) || Lines.Any(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Session log files: naming, pruning, listing and parsing the format written by <see cref="FileLoggerProvider"/>.</summary>
public static partial class LogFiles
{
    /// <summary>Name of a program's session logs when it gives none.</summary>
    public const string DefaultName = "client";
    private const string Extension = ".log";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Return a new file path for a session starting at <paramref name="started"/>, e.g. <c>logs/client-20261026-010536.log</c>, named by <paramref name="name"/>, such as <c>server</c>.</summary>
    public static string CreateSessionPath(string directory, DateTime started, string name = DefaultName)
    {
        // Stamp, with a counter when two sessions start in the same second
        string stamp = started.ToString(StampFormat, CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"{name}-{stamp}{Extension}");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(directory, $"{name}-{stamp}-{i}{Extension}");
        // Return path
        return path;
    }

    /// <summary>Return the session logs in <paramref name="directory"/>, newest first: files named like <c>name-20261026-010536.log</c>, whatever the name, so a program's logs of an earlier name are found too.</summary>
    private static IEnumerable<(string Path, DateTime Started)> Sessions(string directory)
    {
        // Named by a stamp after the name
        if (!Directory.Exists(directory)) return [];
        List<(string, DateTime)> sessions = [];
        foreach (string path in Directory.GetFiles(directory, $"*{Extension}"))
        {
            if (SessionName().Match(Path.GetFileName(path)) is not { Success: true } match) continue;
            DateTime started = DateTime.ParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture);
            sessions.Add((path, started));
        }
        // Return newest first, by stamp and then name, which orders a counter after its first
        return sessions.OrderByDescending(s => s.Item2).ThenByDescending(s => Path.GetFileName(s.Item1), StringComparer.Ordinal);
    }

    /// <summary>Delete the oldest session logs so that at most <paramref name="keep"/> remain, never <paramref name="except"/>.</summary>
    public static void Prune(string directory, int keep, string? except = null)
    {
        // Newest first
        IEnumerable<string> old = Sessions(directory).Select(s => s.Path).Skip(Math.Max(1, keep)).ToList();
        foreach (string path in old)
        {
            if (path == except) continue;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open elsewhere; try again next start
            }
        }
    }

    /// <summary>List session logs newest first, then the single log of older versions at <paramref name="legacyPath"/> when it exists.</summary>
    public static IReadOnlyList<LogFileInfo> List(string directory, string? legacyPath = null)
    {
        // Session logs
        List<LogFileInfo> files = [.. Sessions(directory).Select(s => new LogFileInfo { Path = s.Path, Started = s.Started, Size = new FileInfo(s.Path).Length })];

        // Older single log
        if (legacyPath is not null && File.Exists(legacyPath)) files.Add(new LogFileInfo { Path = legacyPath, Started = File.GetLastWriteTime(legacyPath), Size = new FileInfo(legacyPath).Length, Legacy = true });
        // Return files
        return files;
    }

    /// <summary>Read a log file that may still be written to.</summary>
    public static IReadOnlyList<LogEntry> Read(string path)
    {
        // Share with the writer
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        List<string> lines = [];
        while (reader.ReadLine() is string line) lines.Add(line);
        // Return entries
        return Parse(lines);
    }

    /// <summary>Group lines into entries: a line starting with time and level begins one, other lines continue it.</summary>
    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> lines)
    {
        // Collect lines per entry
        List<LogEntry> entries = [];
        List<string>? current = null;
        Match? header = null;
        foreach (string line in lines)
        {
            Match match = HeaderPattern().Match(line);
            if (match.Success || current is null)
            {
                if (current is not null) entries.Add(CreateEntry(header, current));
                current = [];
                header = match.Success ? match : null;
            }
            current.Add(line);
        }
        if (current is not null) entries.Add(CreateEntry(header, current));
        // Return entries
        return entries;
    }

    /// <summary>Build an entry from its header match and lines.</summary>
    private static LogEntry CreateEntry(Match? header, List<string> lines)
    {
        // Without a header, only the lines
        if (header is null) return new LogEntry { Lines = lines };
        // Return parsed entry
        LogLevel? level = Enum.TryParse(header.Groups["level"].Value, out LogLevel parsed) ? parsed : null;
        return new LogEntry { Time = header.Groups["time"].Value, Level = level, Category = header.Groups["category"].Value, Lines = lines };
    }

    /// <summary>First line of an entry: <c>2026-10-26 01:05:36.901 [Information] Category: message</c>.</summary>
    [GeneratedRegex(@"^(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(?<level>\w+)\] (?<category>[^:]+):")]
    private static partial Regex HeaderPattern();

    /// <summary>A session log's file name: a name, the stamp, a counter for sessions of the same second, and the extension.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.]+-(?<stamp>\d{8}-\d{6})(-\d+)?\.log$")]
    private static partial Regex SessionName();
}
