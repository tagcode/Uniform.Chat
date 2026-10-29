namespace Uniform.Matrix.Client.Services;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Background service that prepares the database, connects, then runs sync and the outbox until shutdown.</summary>
public sealed class MatrixWorker : BackgroundService
{
    private readonly ClientStore store;
    private readonly ClientSession session;
    private readonly SyncProcessor processor;
    private readonly OutboxSender outbox;
    private readonly MatrixOptions options;
    private readonly ConnectionStatus status;
    private readonly CryptoService crypto;
    private readonly NetworkMonitor network;
    private readonly ILogger<MatrixWorker> logger;

    /// <summary>Create the worker.</summary>
    public MatrixWorker(ClientStore store, ClientSession session, SyncProcessor processor, OutboxSender outbox, IOptions<MatrixOptions> options, ConnectionStatus status, CryptoService crypto, NetworkMonitor network, ILogger<MatrixWorker> logger)
    {
        // Store dependencies
        this.store = store;
        this.session = session;
        this.processor = processor;
        this.outbox = outbox;
        this.options = options.Value;
        this.status = status;
        this.crypto = crypto;
        this.network = network;
        this.logger = logger;
    }

    /// <summary>Run until <paramref name="stoppingToken"/> is cancelled; fatal errors such as a rejected login end it with <see cref="ConnectionState.Failed"/>, which the UI reports.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run, reporting fatal errors to the UI
        try
        {
            status.Set(ConnectionState.Connecting, "Opening local storage");
            await store.MigrateAsync(stoppingToken);
            status.Set(ConnectionState.Connecting, "Logging in");
            MatrixClient client = await session.ConnectAsync(stoppingToken);
            status.Set(ConnectionState.Connecting, "Preparing encryption");
            await client.RetryTransientAsync(async token => { await crypto.StartAsync(client, token); return true; }, OnSyncRetry, stoppingToken);
            int decrypted = await processor.RetryStoredEncryptedAsync(stoppingToken);
            if (decrypted > 0) logger.LogInformation("Decrypted {Count} stored events with known keys", decrypted);

            // On another network, sync and send again at once, rather than wait for requests on the old one to time out
            void OnNetworkChanged()
            {
                client.InterruptSync();
                outbox.Wake();
            }
            network.Changed += OnNetworkChanged;
            try { await Task.WhenAll(SyncAsync(client, stoppingToken), outbox.RunAsync(client, stoppingToken)); }
            finally { network.Changed -= OnNetworkChanged; }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Matrix worker failed");
            status.Set(ConnectionState.Failed, ex.Message);
        }
    }

    /// <summary>Sync from the stored position; the first request asks for full state to rebuild room state in memory, and is retried, as the app may start without network.</summary>
    private async Task SyncAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Rebuild state from stored position
        status.Set(ConnectionState.Connecting, "Syncing");
        string userId = client.UserId!;
        StoredAccount? account = await store.LoadAccountAsync(cancellationToken);
        string filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = options.TimelineLimit, LazyLoadMembers = true }, State = new RoomEventFilter { LazyLoadMembers = true } } }.ToJson();
        SyncResponse first = await client.RetryTransientAsync(token => client.SyncAsync(account?.NextBatch, filter: filter, fullState: true, cancellationToken: token), OnSyncRetry, cancellationToken);
        await processor.ProcessAsync(first, userId, cancellationToken);
        logger.LogInformation("Initial sync done: {Rooms} joined rooms", first.Rooms.Join.Count);
        status.Set(ConnectionState.Connected);

        // Follow updates; a success after retries restores the connected state
        await foreach (SyncResponse sync in client.SyncLoopAsync(first.NextBatch, filter, options.SyncTimeout, OnSyncRetry, cancellationToken))
        {
            await processor.ProcessAsync(sync, userId, cancellationToken);
            if (status.State != ConnectionState.Connected) status.Set(ConnectionState.Connected);
        }
    }

    /// <summary>Log a failed sync and show it as retrying.</summary>
    private void OnSyncRetry(Exception exception, TimeSpan wait)
    {
        // Log and publish
        logger.LogWarning("Sync failed, retrying in {Wait}: {Error}", wait, exception.Message);
        status.Set(ConnectionState.Retrying, $"{exception.Message}; retrying in {wait.TotalSeconds:F0} s");
    }
}
