namespace Uniform.Matrix.Server.Tests;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

/// <summary>Starting and stopping: a stop does not wait for long-polling clients, one process uses a data directory, a migration lock left by a crash does not block the next start, and the database's connections are set up for concurrent use.</summary>
public class LifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sync waiting for news answers at once when the server starts to stop, so that stopping need not wait for its timeout.</summary>
    [Fact]
    public async Task AnswerLongPollWhenStopping()
    {
        // Arrange: a client waiting up to 30 seconds
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
        MatrixClient alice = server.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        SyncResponse initial = await alice.SyncAsync(cancellationToken: Ct);
        Stopwatch watch = Stopwatch.StartNew();
        Task<SyncResponse> waiting = alice.SyncAsync(initial.NextBatch, TimeSpan.FromSeconds(30), cancellationToken: Ct);
        await Task.Delay(300, Ct);

        // Act: the host starts to stop, as on SIGTERM
        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        SyncResponse answer = await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert: answered long before the timeout, with the same position as nothing happened
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The sync took {watch.Elapsed}.");
        Assert.Equal(initial.NextBatch, answer.NextBatch);
    }

    /// <summary>A second process, or server, on a data directory is refused, naming the process that has it; the directory is free again once released.</summary>
    [Fact]
    public async Task RefuseSecondInstance()
    {
        // Arrange
        string directory = Directory.CreateTempSubdirectory("matrix-server-lock-").FullName;
        try
        {
            // Act and assert: the lock alone
            using (DataDirectoryLock first = DataDirectoryLock.Acquire(directory))
            {
                DataDirectoryInUseException refused = Assert.Throws<DataDirectoryInUseException>(() => DataDirectoryLock.Acquire(directory));
                Assert.Equal(Environment.ProcessId, refused.ProcessId);
                Assert.Contains(directory, refused.Message);
            }
            DataDirectoryLock.Acquire(directory).Dispose();

            // Act and assert: a running server keeps another off its directory, and lets it on once stopped
            Dictionary<string, string?> settings = new() { ["Storage:DataDirectory"] = directory };
            await using (TestHomeserver running = await TestHomeserver.StartAsync(settings))
            {
                await Assert.ThrowsAsync<DataDirectoryInUseException>(() => TestHomeserver.StartAsync(settings));
            }
            await using TestHomeserver next = await TestHomeserver.StartAsync(settings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A database migration lock left by a server killed while migrating is removed at the next start instead of being waited for forever.</summary>
    [Fact]
    public async Task StartPastLeftoverMigrationLock()
    {
        // Arrange: a migrated database with a lock row, as a crash leaves it
        string directory = Directory.CreateTempSubdirectory("matrix-server-crash-").FullName;
        Dictionary<string, string?> settings = new() { ["Storage:DataDirectory"] = directory };
        try
        {
            await using (TestHomeserver first = await TestHomeserver.StartAsync(settings)) { }
            string database = $"Data Source={Path.Combine(directory, "server.db")}";
            await ExecuteAsync(database, "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL); INSERT INTO \"__EFMigrationsLock\" VALUES (1, '2026-11-09 19:28:51');");

            // Act
            await using TestHomeserver restarted = await TestHomeserver.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // Assert: started, and the lock is gone
            Assert.Equal(0L, await ExecuteAsync(database, "SELECT count(*) FROM \"__EFMigrationsLock\""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Run <paramref name="sql"/> on the database and return its first value.</summary>
    private static async Task<object?> ExecuteAsync(string connectionString, string sql)
    {
        // A connection of its own, closed at once
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(Ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        // Return value
        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>The server's connections use the write-ahead log, wait for locks, and sync commits only at checkpoints.</summary>
    [Fact]
    public async Task SetUpDatabaseConnections()
    {
        // Arrange: a connection as the server opens it
        await using TestHomeserver server = await TestHomeserver.StartAsync();
        await using ServerDbContext db = await server.Services.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(Ct);
        await db.Database.OpenConnectionAsync(Ct);

        // Act
        string journal = await PragmaAsync(db, "journal_mode");
        string timeout = await PragmaAsync(db, "busy_timeout");
        string synchronous = await PragmaAsync(db, "synchronous");

        // Assert: WAL, 30 s, NORMAL
        Assert.Equal("wal", journal);
        Assert.Equal("30000", timeout);
        // Done
        Assert.Equal("1", synchronous);
    }

    /// <summary>Return the value of <paramref name="pragma"/> on the context's open connection.</summary>
    private static async Task<string> PragmaAsync(ServerDbContext db, string pragma)
    {
        // One value
        await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        // Return it as text
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
