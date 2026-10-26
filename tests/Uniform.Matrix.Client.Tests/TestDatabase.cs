namespace Uniform.Matrix.Client.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Client.Storage;

/// <summary>In-memory SQLite database that lives as long as this object; every context shares the one open connection.</summary>
internal sealed class TestDatabase : IDbContextFactory<ClientDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<ClientDbContext> options;

    /// <summary>Open the connection; call <see cref="CreateStoreAsync"/> to migrate.</summary>
    public TestDatabase()
    {
        // Keep the connection open, as an in-memory database dies with it
        connection.Open();
        options = new DbContextOptionsBuilder<ClientDbContext>().UseSqlite(connection).Options;
    }

    /// <summary>Create a context on the shared connection.</summary>
    public ClientDbContext CreateDbContext() => new(options);

    /// <summary>Return a migrated store over this database.</summary>
    public async Task<ClientStore> CreateStoreAsync()
    {
        // Migrate like the app does
        ClientStore store = new(this, TimeProvider.System);
        await store.MigrateAsync(TestContext.Current.CancellationToken);
        // Return store
        return store;
    }

    /// <summary>Close the connection, dropping the database.</summary>
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
