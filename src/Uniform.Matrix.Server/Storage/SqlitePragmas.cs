namespace Uniform.Matrix.Server.Storage;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Sets the SQLite settings that last only for a connection, on each connection as it opens: the <see href="https://www.sqlite.org/pragma.html#pragma_busy_timeout">busy timeout</see>, so a write that meets another connection's lock waits for it inside SQLite; and <see href="https://www.sqlite.org/pragma.html#pragma_synchronous">synchronous</see> NORMAL, so commits do not each wait for the disk.</summary>
/// <remarks>
/// Microsoft.Data.Sqlite retries a busy statement only while stepping it. EF Core writes with <c>RETURNING</c>, whose commit happens when the reader is reset, where it does not retry; without a busy timeout, such a write fails at once with "database is locked" when another connection reads at that moment.
/// In write-ahead log mode, which <see cref="DatabaseMigrator"/> sets, NORMAL syncs the log only at checkpoints: a crash of the process loses nothing, and a power cut or a crash of the system may lose the last commits but leaves the database consistent.
/// </remarks>
internal sealed class SqlitePragmas : DbConnectionInterceptor
{
    /// <summary>The pragmas: a lock waits up to 30 seconds; commits are synced at checkpoints.</summary>
    internal const string Statements = "PRAGMA busy_timeout = 30000; PRAGMA synchronous = NORMAL;";

    /// <summary>Set the pragmas on a connection opened synchronously.</summary>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        // The pragma
        using DbCommand command = Pragma(connection);
        // Done
        command.ExecuteNonQuery();
    }

    /// <summary>Set the pragmas on a connection opened asynchronously.</summary>
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        // The pragma
        await using DbCommand command = Pragma(connection);
        // Done
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Return the command that sets the pragmas.</summary>
    private static DbCommand Pragma(DbConnection connection)
    {
        // Both in one command
        DbCommand command = connection.CreateCommand();
        command.CommandText = Statements;
        // Return command
        return command;
    }
}
