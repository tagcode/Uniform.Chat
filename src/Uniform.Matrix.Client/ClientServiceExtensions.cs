namespace Uniform.Matrix.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Hosting;

/// <summary>Registers the headless Matrix client on a .NET host: options, session logging, storage, encryption, sync and outbox.</summary>
/// <remarks>The application adds <see cref="HostingExtensions.AddSettings"/> first, and its own <see cref="ICredentialPrompt"/> for the first login.</remarks>
/// <example><code><![CDATA[
/// HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
/// builder.AddSettings();
/// builder.AddMatrixClient();
/// builder.Services.AddSingleton<ICredentialPrompt, MyPrompt>();
/// ]]></code></example>
public static class ClientServiceExtensions
{
    /// <summary>Add the client's options and services, and log each session to a file under the data directory.</summary>
    public static IHostApplicationBuilder AddMatrixClient(this IHostApplicationBuilder builder)
    {
        // Options, listed in the settings window in this order
        builder.Services.AddOptionSection<MatrixOptions>(builder.Configuration, MatrixOptions.Section);
        builder.Services.AddOptionSection<StorageOptions>(builder.Configuration, StorageOptions.Section);

        // One log file per session
        StorageOptions storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        builder.AddSessionLogging(storage.ResolveLogDirectory(builder.Environment.ApplicationName), storage.LogFilesKept);

        // Storage
        IServiceCollection services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AccountPaths>();
        services.AddDbContextFactory<ClientDbContext>((provider, db) =>
        {
            AccountPaths paths = provider.GetRequiredService<AccountPaths>();
            Directory.CreateDirectory(paths.Directory);
            db.UseSqlite($"Data Source={paths.DatabasePath}");
        });
        services.AddSingleton<ClientStore>();
        services.AddSingleton<EfCryptoStore>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<MediaCache>();

        // Matrix; HTTP timeout must outlast the sync long-poll
        services.AddHttpClient(ClientSession.HttpClientName, (provider, http) => http.Timeout = provider.GetRequiredService<IOptions<MatrixOptions>>().Value.SyncTimeout + TimeSpan.FromSeconds(30));
        services.AddSingleton<ClientSession>();
        services.AddSingleton<SyncProcessor>();
        services.AddSingleton<OutboxSender>();
        services.AddSingleton<ConnectionStatus>();
        services.AddHostedService<MatrixWorker>();
        // Return builder
        return builder;
    }
}
