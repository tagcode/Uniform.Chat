namespace Uniform.Matrix.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Endpoints;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Media;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>Registers and maps the homeserver on an ASP.NET Core application.</summary>
/// <example><code><![CDATA[
/// WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
/// builder.AddSettings();
/// builder.AddMatrixServer();
/// WebApplication app = builder.Build();
/// app.MapMatrixServer();
/// await app.RunAsync();
/// ]]></code></example>
public static class ServerServiceExtensions
{
    /// <summary>Add the server's options and services.</summary>
    public static IHostApplicationBuilder AddMatrixServer(this IHostApplicationBuilder builder)
    {
        // Options, listed in the settings window in this order
        builder.Services.AddOptionSection<ServerOptions>(builder.Configuration, ServerOptions.Section);
        builder.Services.AddOptionSection<ServerStorageOptions>(builder.Configuration, ServerStorageOptions.Section);
        builder.Services.AddOptionSection<MediaOptions>(builder.Configuration, MediaOptions.Section);

        // JSON like the rest of the protocol
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = MatrixJson.Options.PropertyNamingPolicy;
            json.SerializerOptions.DefaultIgnoreCondition = MatrixJson.Options.DefaultIgnoreCondition;
        });

        // Bad JSON reaches the error middleware instead of an empty 400
        builder.Services.Configure<RouteHandlerOptions>(handler => handler.ThrowOnBadRequest = true);

        // Database in the data directory, migrated at start
        ServerStorageOptions storage = builder.Configuration.GetSection(ServerStorageOptions.Section).Get<ServerStorageOptions>() ?? new ServerStorageOptions();
        string dataDirectory = storage.ResolveDataDirectory(builder.Environment.ApplicationName);
        Directory.CreateDirectory(dataDirectory);
        builder.Services.AddDbContextFactory<ServerDbContext>(db => db.UseSqlite($"Data Source={Path.Combine(dataDirectory, storage.DatabaseFileName)}"));
        builder.Services.AddHostedService<DatabaseMigrator>();

        // Services
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerStatus>();
        builder.Services.AddSingleton<AccountService>();
        builder.Services.AddSingleton<AdminService>();
        builder.Services.AddSingleton<UserInteractiveAuth>();
        builder.Services.AddSingleton<ServerKeys>();
        builder.Services.AddSingleton<ServerStream>();
        builder.Services.AddSingleton<StateCache>();
        builder.Services.AddSingleton<EventSender>();
        builder.Services.AddSingleton<RoomService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<AccountDataService>();
        builder.Services.AddSingleton<ReceiptService>();
        builder.Services.AddSingleton<TypingService>();
        builder.Services.AddSingleton<ProfileService>();
        builder.Services.AddSingleton<PresenceService>();
        builder.Services.AddSingleton<DirectoryService>();
        builder.Services.AddSingleton<MediaService>();
        builder.Services.AddSingleton<DeviceKeyService>();
        builder.Services.AddSingleton<ToDeviceService>();
        builder.Services.AddSingleton<KeyBackupService>();
        // Return builder
        return builder;
    }

    /// <summary>Add the Matrix middleware and map every endpoint.</summary>
    public static WebApplication MapMatrixServer(this WebApplication app)
    {
        // Counting, CORS and errors around every request
        ServerStatus status = app.Services.GetRequiredService<ServerStatus>();
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Uniform.Matrix.Server.Http");
        app.Use(async (context, next) =>
        {
            status.RequestStarted();
            try
            {
                await MatrixHttp.HandleAsync(context, next, logger);
            }
            finally
            {
                status.RequestFinished(context.Response.StatusCode);
            }
        });

        // Endpoints by spec area
        app.MapDiscovery();
        app.MapAccounts();
        app.MapDevices();
        app.MapRooms();
        app.MapSync();
        app.MapAccountData();
        app.MapActivity();
        app.MapProfiles();
        app.MapDirectory();
        app.MapMedia();
        app.MapKeys();
        app.MapKeyBackup();

        // Addresses in the log once listening
        app.Lifetime.ApplicationStarted.Register(() => logger.LogInformation("Serving {ServerName} at {Addresses}", app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ServerName, string.Join(", ", app.Urls)));
        // Return application
        return app;
    }
}
