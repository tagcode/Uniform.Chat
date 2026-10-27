namespace Uniform.Matrix.Server.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Client;

/// <summary>A homeserver running in memory with its own data directory, reached through <see cref="CreateClient"/>; dispose to stop it and delete its data.</summary>
internal sealed class TestHomeserver : IAsyncDisposable
{
    /// <summary>Server name of test servers.</summary>
    public const string ServerName = "test.local";
    private readonly WebApplication app;
    private readonly string directory;

    /// <summary>Wrap a started server.</summary>
    private TestHomeserver(WebApplication app, string directory)
    {
        // Store parts
        this.app = app;
        this.directory = directory;
    }

    /// <summary>Base address clients use.</summary>
    public Uri BaseAddress { get; } = new("http://localhost/");

    /// <summary>Start a server; <paramref name="settings"/> override configuration, e.g. <c>Server:PublicBaseUrl</c>, <paramref name="time"/> replaces the clock, e.g. with a fake one to move time on, and <paramref name="configureServices"/> changes services, e.g. the HTTP handler of push gateways.</summary>
    public static async Task<TestHomeserver> StartAsync(IReadOnlyDictionary<string, string?>? settings = null, TimeProvider? time = null, Action<IServiceCollection>? configureServices = null)
    {
        // Configuration with a private data directory
        string directory = Directory.CreateTempSubdirectory("matrix-server-").FullName;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Matrix.Server.Tests", EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        Dictionary<string, string?> values = new() { ["Server:ServerName"] = ServerName, ["Storage:DataDirectory"] = directory };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>()) values[key] = value;
        builder.Configuration.AddInMemoryCollection(values);

        // Server as the app runs it, logging into the test output
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TestOutputLoggerProvider());
        builder.AddMatrixServer();
        if (time is not null) builder.Services.Replace(ServiceDescriptor.Singleton(time));
        configureServices?.Invoke(builder.Services);
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        await app.StartAsync(TestContext.Current.CancellationToken);
        // Return running server
        return new TestHomeserver(app, directory);
    }

    /// <summary>Services of the server, for tests that call the engine directly.</summary>
    public IServiceProvider Services => app.Services;

    /// <summary>Return an HTTP client to the server.</summary>
    public HttpClient CreateHttpClient() => app.GetTestClient();

    /// <summary>Return an HTTP handler into the server, e.g. for other test servers to reach it.</summary>
    public HttpMessageHandler CreateHandler() => app.GetTestServer().CreateHandler();

    /// <summary>Send one request built by <paramref name="configure"/>, e.g. to set the connection's address; return the finished context.</summary>
    public Task<HttpContext> SendAsync(Action<HttpContext> configure) => app.GetTestServer().SendAsync(configure, TestContext.Current.CancellationToken);

    /// <summary>Return a Matrix client to the server, not logged in.</summary>
    public MatrixClient CreateClient() => new(CreateHttpClient(), BaseAddress);

    /// <summary>Stop the server and delete its data.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop the background services first, as disposing alone leaves them running; then remove files
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}
