namespace Uniform.Matrix.Server.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;

/// <summary>Creates or upgrades the schema at start, before the server accepts requests, in write-ahead log mode so reads do not block writes; and flushes the database at stop.</summary>
/// <remarks>It holds the <see cref="DataDirectoryLock"/>, so no other process uses the database meanwhile; a migration lock that a process left behind when it died is therefore removed instead of waited for, as EF Core would wait forever.</remarks>
public sealed class DatabaseMigrator : IHostedService
{
    /// <summary>EF Core's migration lock table in SQLite.</summary>
    private const string LockTable = "__EFMigrationsLock";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ILogger<DatabaseMigrator> logger;

    /// <summary>Use <paramref name="contexts"/> for the database, which <paramref name="directoryLock"/> keeps to this process.</summary>
    /// <param name="contexts">The database.</param>
    /// <param name="directoryLock">The lock of the data directory; taken here so that the host keeps it until disposed.</param>
    /// <param name="logger">Log of leftover locks and the stop.</param>
    public DatabaseMigrator(IDbContextFactory<ServerDbContext> contexts, DataDirectoryLock directoryLock, ILogger<DatabaseMigrator> logger)
    {
        // Store dependencies; the lock is held by the host's container
        this.contexts = contexts;
        this.logger = logger;
        _ = directoryLock;
    }

    /// <summary>Remove a leftover migration lock, switch to the write-ahead log, and apply pending migrations; throw with advice when the database has tables the migration history does not know.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Leftover lock of a process that died while migrating
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await RemoveLeftoverLockAsync(db, cancellationToken);

        // Write-ahead log, kept in the file, before anything else writes
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);

        // Migrate
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            // A database of a development build whose migrations were later rewritten
            string file = db.Database.GetDbConnection().DataSource;
            throw new InvalidOperationException($"The database {file} was made by an earlier development build, whose migration history no longer matches. Stop the server and delete {file}, with its -wal and -shm files, to start with an empty database.", ex);
        }
    }

    /// <summary>Write the write-ahead log into the database and close its pooled connections, so that the files are complete when the process ends; other databases of the process, e.g. of other servers in tests, keep theirs.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Checkpoint, then close this database's pooled connections
        string? connectionString = null;
        try
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            connectionString = db.Database.GetConnectionString();
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or OperationCanceledException or InvalidOperationException)
        {
            // On a database in use or a stop cut short: SQLite checkpoints when the last connection closes
            logger.LogWarning("Checkpointing the database at stop failed: {Error}", ex.Describe());
        }
        // Done
        if (connectionString is null) return;
        using SqliteConnection pooled = new(connectionString);
        SqliteConnection.ClearPool(pooled);
    }

    /// <summary>Delete rows of EF Core's migration lock, which only a process that died while migrating leaves, and warn.</summary>
    private async Task RemoveLeftoverLockAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        // Only when the table exists, i.e. a migration has run before
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using SqliteCommand exists = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
            exists.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{LockTable}'";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return;
            int removed = await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{LockTable}\"", cancellationToken);
            if (removed > 0) logger.LogWarning("Removed a database migration lock that a server left when it stopped while migrating");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
