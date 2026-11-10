namespace Uniform.Chat.Server.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Server;
using Uniform.Matrix.Server.Http;

/// <summary>A homeserver on Kestrel, at a local TCP port and an admin socket in its own folder, as the service runs; dispose to stop it and delete its data.</summary>
internal sealed class SocketHomeserver : IAsyncDisposable
{
    /// <summary>Server name of test servers.</summary>
    public const string ServerName = "test.local";
    private readonly WebApplication app;
    private readonly string directory;

    /// <summary>Wrap a started server.</summary>
    private SocketHomeserver(WebApplication app, string directory, Uri address)
    {
        // Parts
        this.app = app;
        this.directory = directory;
        Address = address;
    }

    /// <summary>The TCP address clients use.</summary>
    public Uri Address { get; }

    /// <summary>The admin socket.</summary>
    public string Socket => Path.Combine(directory, "admin.sock");

    /// <summary>Start a server in a new folder.</summary>
    public static async Task<SocketHomeserver> StartAsync()
    {
        // Data and socket in a private folder; a free port
        string directory = Directory.CreateTempSubdirectory("uniform-chat-manage-").FullName;
        string socket = Path.Combine(directory, "admin.sock");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Chat.Server.Tests", EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:ServerName"] = ServerName, ["Storage:DataDirectory"] = Path.Combine(directory, "data") });
        builder.WebHost.UseUrls($"http://127.0.0.1:0;http://unix:{socket}");
        builder.Services.Configure<KestrelServerOptions>(kestrel => kestrel.MarkAdminSocket(socket));
        builder.Logging.ClearProviders();

        // The server, started
        builder.AddMatrixServer();
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        await app.StartAsync(TestContext.Current.CancellationToken);
        // Return running server at its port
        return new SocketHomeserver(app, directory, new Uri(app.Urls.First(u => u.StartsWith("http://127.", StringComparison.Ordinal)) + "/"));
    }

    /// <summary>Return a client over TCP, not logged in.</summary>
    public MatrixClient CreateClient() => new(new HttpClient { BaseAddress = Address }, Address);

    /// <summary>Stop the server and delete its data.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop, then the files
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
        // Done
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }
}
