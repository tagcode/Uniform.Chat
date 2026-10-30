namespace Uniform.Chat.Avalonia.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>The client engine of one signed-in account, running in its own .NET host until disposed.</summary>
/// <remarks>The engine needs the user ID when it starts, so the app starts one connection per sign-in instead of one host for its whole life.</remarks>
public sealed class ClientConnection : IAsyncDisposable
{
    private readonly IHost host;

    /// <summary>Wrap a started host.</summary>
    private ClientConnection(IHost host, string userId)
    {
        // Engine services
        this.host = host;
        UserId = userId;
        Session = host.Services.GetRequiredService<ClientSession>();
        Processor = host.Services.GetRequiredService<SyncProcessor>();
        Outbox = host.Services.GetRequiredService<OutboxSender>();
        Store = host.Services.GetRequiredService<ClientStore>();
        Status = host.Services.GetRequiredService<ConnectionStatus>();
        Media = host.Services.GetRequiredService<MediaCache>();
        Crypto = host.Services.GetRequiredService<CryptoService>();
        Calls = host.Services.GetRequiredService<CallService>();
        Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Uniform.Chat.Avalonia");
    }

    /// <summary>Signed-in user.</summary>
    public string UserId { get; }
    /// <summary>Login and the logged-in client.</summary>
    public ClientSession Session { get; }
    /// <summary>Room state and the stream of sync changes.</summary>
    public SyncProcessor Processor { get; }
    /// <summary>Messages waiting to be sent.</summary>
    public OutboxSender Outbox { get; }
    /// <summary>Local database.</summary>
    public ClientStore Store { get; }
    /// <summary>Connection state for the status line.</summary>
    public ConnectionStatus Status { get; }
    /// <summary>Downloaded media and thumbnails.</summary>
    public MediaCache Media { get; }
    /// <summary>End-to-end encryption.</summary>
    public CryptoService Crypto { get; }
    /// <summary>Voice calls.</summary>
    public CallService Calls { get; }
    /// <summary>Log of the user interface, written to the session log.</summary>
    public ILogger Logger { get; }

    /// <summary>Start the engine for <paramref name="signIn"/>; the login itself runs in the background, see <see cref="WaitForLoginAsync"/>.</summary>
    public static async Task<ClientConnection> StartAsync(GuiPlatform platform, SignIn signIn, CancellationToken cancellationToken = default)
    {
        // Host named after the app, so data and settings folders are its own
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = platform.ApplicationName, ContentRootPath = AppContext.BaseDirectory, Args = [] });
        builder.Logging.ClearProviders();
        builder.AddSettings();

        // Account of this sign-in over the settings file
        Dictionary<string, string?> values = new(platform.Settings)
        {
            [$"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}"] = signIn.UserId,
            [$"{MatrixOptions.Section}:{nameof(MatrixOptions.DeviceName)}"] = platform.DeviceName,
        };

        // Log levels the terminal client sets in its appsettings.json
        foreach ((string category, string level) in new[] { ("Default", "Information"), ("Microsoft", "Warning"), ("Microsoft.EntityFrameworkCore", "Warning"), ("System.Net.Http", "Warning") })
        {
            values.TryAdd($"Logging:LogLevel:{category}", level);
        }
        if (!string.IsNullOrWhiteSpace(signIn.Homeserver)) values[$"{MatrixOptions.Section}:{nameof(MatrixOptions.Homeserver)}"] = signIn.Homeserver.Trim();
        builder.Configuration.AddInMemoryCollection(values);

        // Engine with the form's password
        builder.AddMatrixClient();
        builder.Services.AddSingleton<ICredentialPrompt>(new GuiCredentialPrompt(signIn.Password));
        builder.Services.AddSingleton<CallService>();
        platform.ConfigureServices?.Invoke(builder.Services);
        IHost host = builder.Build();
        await host.StartAsync(cancellationToken);
        // Return connection
        return new ClientConnection(host, signIn.UserId);
    }

    /// <summary>Return a user's display name from the rooms shared with them, or the user ID when none is known.</summary>
    public string DisplayNameOf(string userId) =>
        Processor.Read(rooms => rooms?.Rooms.Values.Select(room => room.Members.TryGetValue(userId, out RoomMemberInfo? member) ? member.DisplayName : null).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))) ?? userId;

    /// <summary>Wait until the engine has logged in; throw with the engine's reason when it fails, e.g. a wrong password.</summary>
    public async Task<MatrixClient> WaitForLoginAsync(CancellationToken cancellationToken = default)
    {
        // Failure as a task, also when it happened already
        TaskCompletionSource<string?> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(ConnectionState state, string? message) { if (state == ConnectionState.Failed) failed.TrySetResult(message); }
        Status.Changed += OnChanged;
        try
        {
            if (Status.State == ConnectionState.Failed) failed.TrySetResult(Status.Message);
            Task first = await Task.WhenAny(Session.WhenReady, failed.Task).WaitAsync(cancellationToken);
            if (first == failed.Task) throw new InvalidOperationException(await failed.Task ?? "Signing in failed.");
            // Return client
            return await Session.WhenReady;
        }
        finally
        {
            Status.Changed -= OnChanged;
        }
    }

    /// <summary>Log out on the server, which deletes this device, then stop the engine.</summary>
    public async Task LogOutAsync()
    {
        // Best effort: an unreachable server still ends the local session; a deleted account has none left
        if (Session.WhenReady.IsCompletedSuccessfully && (await Session.WhenReady).Session is not null)
        {
            try
            {
                await (await Session.WhenReady).LogoutAsync();
            }
            catch (Exception ex) when (ex is MatrixException or HttpRequestException or TaskCanceledException)
            {
                Logger.LogWarning("Logging out on the server failed: {Error}", ex.Message);
            }
        }
        await Store.ClearAccessTokenAsync();
    }

    /// <summary>Stop the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop services, then free them
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            host.Dispose();
        }
    }
}
