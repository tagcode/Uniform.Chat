namespace Uniform.Matrix.Server.Storage;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Sets SQLite's <see href="https://www.sqlite.org/pragma.html#pragma_busy_timeout">busy timeout</see> on each connection as it opens, so a write that meets another connection's lock waits for it inside SQLite.</summary>
/// <remarks>Microsoft.Data.Sqlite retries a busy statement only while stepping it. EF Core writes with <c>RETURNING</c>, whose commit happens when the reader is reset, where it does not retry; without a busy timeout, such a write fails at once with "database is locked" when another connection reads at that moment.</remarks>
internal sealed class SqliteBusyTimeout : DbConnectionInterceptor
{
    /// <summary>How long a statement waits for a lock, in milliseconds.</summary>
    private const int TimeoutMilliseconds = 30_000;

    /// <summary>Set the timeout on a connection opened synchronously.</summary>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        // The pragma
        using DbCommand command = Pragma(connection);
        // Done
        command.ExecuteNonQuery();
    }

    /// <summary>Set the timeout on a connection opened asynchronously.</summary>
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        // The pragma
        await using DbCommand command = Pragma(connection);
        // Done
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Return the command that sets the timeout.</summary>
    private static DbCommand Pragma(DbConnection connection)
    {
        // One statement
        DbCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {TimeoutMilliseconds};";
        // Return command
        return command;
    }
}
