namespace Uniform.Matrix.Client.Services;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Client.Localization;

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
            status.Set(ConnectionState.Connecting, Strings.OpeningStorage);
            await store.MigrateAsync(stoppingToken);
            status.Set(ConnectionState.Connecting, Strings.LoggingIn);
            MatrixClient client = await session.ConnectAsync(stoppingToken);
            status.Set(ConnectionState.Connecting, Strings.PreparingEncryption);
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
            try { await Task.WhenAll(SyncUntilStoppedAsync(client, stoppingToken), outbox.RunAsync(client, stoppingToken)); }
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

    /// <summary>Sync until stopped; a sync that fails to be processed, e.g. when the server refuses encryption keys, is logged and synced again from the stored position after a wait, rather than ending syncing for the session. A rejected login still ends it.</summary>
    private async Task SyncUntilStoppedAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Waits doubling from 5 s to 5 min while failures repeat
        TimeSpan wait = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                await SyncAsync(client, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !(exception is MatrixException { Errcode: ErrorCodes.UnknownToken or ErrorCodes.MissingToken }))
            {
                // Logged, shown as retrying, then again
                logger.LogError(exception, "Processing a sync failed; syncing again in {Wait}", wait);
                status.Set(ConnectionState.Retrying, Texts.Format(Strings.RetryingIn, exception.Message, wait.TotalSeconds));
                await Task.Delay(wait, cancellationToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }
    }

    /// <summary>Sync from the stored position; the first request asks for full state to rebuild room state in memory, and is retried, as the app may start without network.</summary>
    private async Task SyncAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Rebuild state from stored position
        status.Set(ConnectionState.Connecting, "Syncing");
        string userId = client.UserId!;
        if (await processor.RestoreRoomsAsync(userId, cancellationToken)) logger.LogInformation("Rooms listed from the last session until the first sync");
        StoredAccount? account = await store.LoadAccountAsync(cancellationToken);
        string filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = options.TimelineLimit, LazyLoadMembers = true }, State = new RoomEventFilter { LazyLoadMembers = true } } }.ToJson();
        SyncResponse first = await client.RetryTransientAsync(token => client.SyncAsync(account?.NextBatch, filter: filter, fullState: true, cancellationToken: token), OnSyncRetry, cancellationToken);
        await processor.ProcessAsync(first, userId, cancellationToken);
        logger.LogInformation("Initial sync done: {Rooms} joined rooms", first.Rooms.Join.Count);
        status.Set(ConnectionState.Connected);
        _ = processor.LoadMembersOfUnnamedRoomsAsync(client, cancellationToken);

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
        logger.LogWarning("Sync failed, retrying in {Wait}: {Error}", wait, exception.Describe());
        status.Set(ConnectionState.Retrying, Texts.Format(Strings.RetryingIn, exception.Message, wait.TotalSeconds));
    }
}
