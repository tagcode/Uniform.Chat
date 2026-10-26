namespace Uniform.Matrix.Server.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>Creates or upgrades the schema at start, before the server accepts requests.</summary>
public sealed class DatabaseMigrator : IHostedService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;

    /// <summary>Use <paramref name="contexts"/> for the database.</summary>
    public DatabaseMigrator(IDbContextFactory<ServerDbContext> contexts)
    {
        // Store dependency
        this.contexts = contexts;
    }

    /// <summary>Apply pending migrations; throw with advice when the database has tables the migration history does not know.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Migrate
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
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

    /// <summary>Nothing to stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
