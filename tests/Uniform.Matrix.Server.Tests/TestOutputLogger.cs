namespace Uniform.Matrix.Server.Tests;

using Microsoft.Extensions.Logging;

/// <summary>Writes a test server's warnings and errors to the running test's output, so failures show their server-side cause.</summary>
/// <remarks>The output of the test that starts the server is captured then, as requests run outside the test's context.</remarks>
internal sealed class TestOutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper? output = TestContext.Current.TestOutputHelper;

    /// <summary>Create a logger of one category.</summary>
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, output);

    /// <summary>Nothing to release.</summary>
    public void Dispose()
    {
    }

    /// <summary>Logger writing to the captured test output.</summary>
    private sealed class Logger(string category, ITestOutputHelper? output) : ILogger
    {
        /// <summary>Scopes are not recorded.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>Warnings and errors only.</summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        /// <summary>Write one entry with its exception.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Assert enabled and inside a test
            if (!IsEnabled(logLevel)) return;
            output?.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
    }
}
