namespace Uniform.Matrix.Hosting;

using Microsoft.Extensions.Logging;

/// <summary>Writes log lines to a new file per session; the terminal is owned by the UI, so console logging is not an option.</summary>
/// <remarks>Levels are configured like other providers, e.g. <c>Logging:File:LogLevel:Default</c>. Files are named by <see cref="LogFiles.CreateSessionPath"/>, and old ones are pruned at start.</remarks>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter writer;
    private readonly Lock gate = new();

    /// <summary>Start a session log in <paramref name="directory"/>, keeping at most <paramref name="keep"/> session logs.</summary>
    public FileLoggerProvider(string directory, int keep)
    {
        // New file for this session, then drop the oldest
        Directory.CreateDirectory(directory);
        FilePath = LogFiles.CreateSessionPath(directory, DateTime.Now);
        writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        LogFiles.Prune(directory, keep, except: FilePath);
    }

    /// <summary>Log file of this session.</summary>
    public string FilePath { get; }

    /// <summary>Create a logger writing to this file.</summary>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Close the file.</summary>
    public void Dispose()
    {
        // Close under lock so no write races the dispose
        lock (gate) writer.Dispose();
    }

    /// <summary>Write one line.</summary>
    private void Write(string line)
    {
        // Serialize writers
        lock (gate) writer.WriteLine(line);
    }

    /// <summary>Logger of one category.</summary>
    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        /// <summary>Scopes are not recorded.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>Enabled for all levels except <see cref="LogLevel.None"/>; filtering is done by the logging framework.</summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <summary>Format and write one entry.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Assert enabled
            if (!IsEnabled(logLevel)) return;
            // Write line, exception on following lines
            string exceptionText = exception is null ? "" : Environment.NewLine + exception;
            provider.Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}{exceptionText}");
        }
    }
}
