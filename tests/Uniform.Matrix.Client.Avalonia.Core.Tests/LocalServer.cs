namespace Uniform.Matrix.Client.Avalonia.Core.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Server;

/// <summary>Our homeserver in memory, with open registration, for the app to sign in to; dispose to stop it and delete its data.</summary>
internal sealed class LocalServer : IAsyncDisposable
{
    /// <summary>Server name of the test server.</summary>
    public const string ServerName = "test.local";
    /// <summary>Address the app is given; requests never leave the process.</summary>
    public const string Address = "http://localhost/";
    private readonly WebApplication app;
    private readonly string directory;

    /// <summary>Wrap a started server.</summary>
    private LocalServer(WebApplication app, string directory)
    {
        // Store parts
        this.app = app;
        this.directory = directory;
    }

    /// <summary>Start a server with its own data directory.</summary>
    public static async Task<LocalServer> StartAsync()
    {
        // Server as the app runs it, quietly
        string directory = Directory.CreateTempSubdirectory("uniform-matrix-gui-server-").FullName;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Matrix.Client.Avalonia.Core.Tests.Server", EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:ServerName"] = ServerName, ["Server:Registration"] = "Open", ["Storage:DataDirectory"] = directory });
        builder.Logging.ClearProviders();
        builder.AddMatrixServer();
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        await app.StartAsync();
        // Return server
        return new LocalServer(app, directory);
    }

    /// <summary>Register a user with our protocol client and return it logged in.</summary>
    public async Task<MatrixClient> RegisterAsync(string name, string password = "password")
    {
        // Register logs in
        MatrixClient client = new(app.GetTestClient(), new Uri(Address));
        await client.RegisterAsync(name, password);
        // Return client
        return client;
    }

    /// <summary>Return the app's platform for this server: a data folder of its own, and the engine's HTTP calls routed to the server.</summary>
    public GuiPlatform Platform(string dataDirectory) => new()
    {
        ApplicationName = "Uniform.Matrix.Client.Avalonia.Core.Tests",
        DeviceName = "Uniform.Matrix tests",
        Settings = new Dictionary<string, string?> { ["Storage:DataDirectory"] = dataDirectory, ["Matrix:SyncTimeout"] = "00:00:02" },
        ConfigureServices = services => services.AddHttpClient(ClientSession.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler()),
    };

    /// <summary>Stop the server and delete its data.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop, then remove files
        await app.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}
