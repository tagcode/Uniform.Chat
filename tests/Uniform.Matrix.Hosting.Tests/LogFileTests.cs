namespace Uniform.Matrix.Hosting.Tests;

using Microsoft.Extensions.Logging;

/// <summary>Tests of session log files and their parsing.</summary>
public sealed class LogFileTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("matrix-logs-").FullName;

    /// <summary>Lines group into entries with level and category; continuation lines stay with their entry.</summary>
    [Fact]
    public void CanParseEntries()
    {
        // Arrange
        string[] lines =
        [
            "stray line",
            "2026-10-26 01:05:36.901 [Information] Durum.Matrix.Client.Terminal.Services.CryptoService: Encryption ready",
            "2026-10-26 01:05:37.000 [Error] Durum.Matrix.Client.Terminal.Ui.MainWindow: Sending failed",
            "System.InvalidOperationException: boom",
            "   at Somewhere()",
        ];

        // Act
        IReadOnlyList<LogEntry> entries = LogFiles.Parse(lines);

        // Assert
        Assert.Equal(3, entries.Count);
        Assert.Null(entries[0].Level);
        Assert.Equal(LogLevel.Information, entries[1].Level);
        Assert.Equal("Durum.Matrix.Client.Terminal.Services.CryptoService", entries[1].Category);
        Assert.Equal(3, entries[2].Lines.Count);
        Assert.True(entries[2].Matches(LogLevel.Warning, "BOOM"));
        Assert.False(entries[1].Matches(LogLevel.Warning, null));
        Assert.False(entries[2].Matches(LogLevel.Information, "nothing"));
    }

    /// <summary>Each session gets its own file; old ones are pruned, never the current one; listing is newest first with the older single log last.</summary>
    [Fact]
    public void CanKeepSessionFiles()
    {
        // Arrange: five old sessions and an older single log
        for (int i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(directory, $"client-2026010{i}-120000.log"), "x");
        string legacy = Path.Combine(directory, "client.log");
        File.WriteAllText(legacy, "old");

        // Act: a new session keeping three
        using (FileLoggerProvider provider = new(directory, keep: 3))
        {
            provider.CreateLogger("Test").LogWarning("Hello {Name}", "log");
            Assert.Contains("[Warning] Test: Hello log", File.ReadAllText(provider.FilePath));
        }
        IReadOnlyList<LogFileInfo> files = LogFiles.List(directory, legacy);

        // Assert
        Assert.Equal(4, files.Count);
        Assert.True(files[0].Started > new DateTime(2026, 1, 5));
        Assert.Equal(new DateTime(2026, 1, 5, 12, 0, 0), files[1].Started);
        Assert.Equal(new DateTime(2026, 1, 4, 12, 0, 0), files[2].Started);
        Assert.True(files[^1].Legacy);
        Assert.NotEqual(LogFiles.CreateSessionPath(directory, new DateTime(2026, 1, 5, 12, 0, 0)), Path.Combine(directory, "client-20260105-120000.log"));
    }

    /// <summary>Remove the test directory.</summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
