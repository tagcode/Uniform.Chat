namespace Uniform.Matrix.Server.Sync;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Server.Storage;

/// <summary>The server's single stream of persistent changes: each write takes the next position, which sync tokens count.</summary>
/// <remarks>Writes run one at a time, so positions are committed in order and a sync from position <c>n</c> never misses a change at <c>n + 1</c> that commits late.</remarks>
public sealed class ServerStream
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const string CounterName = "stream";
    private long current = -1;

    /// <summary>Create the stream over the database's stored positions.</summary>
    /// <param name="contexts">The database.</param>
    /// <param name="lifetime">The host, whose stopping ends the waits of long-polling clients; null for none.</param>
    public ServerStream(IDbContextFactory<ServerDbContext> contexts, IHostApplicationLifetime? lifetime = null)
    {
        // Store dependencies
        this.contexts = contexts;
        Stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>Cancelled when the server starts to stop: long-polling clients then get their answer at once, so that stopping need not wait for their timeouts.</summary>
    public CancellationToken Stopping { get; }

    /// <summary>Return the latest committed position.</summary>
    public async Task<long> GetCurrentAsync(CancellationToken cancellationToken)
    {
        // Loaded once: the stored counter, or a later position a crash left uncounted
        if (Volatile.Read(ref current) >= 0) return Volatile.Read(ref current);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        long stored = new[]
        {
            await db.Counters.Where(c => c.Name == CounterName).Select(c => (long?)c.Value).SingleOrDefaultAsync(cancellationToken),
            await db.Events.MaxAsync(e => (long?)e.Stream, cancellationToken),
            await db.AccountData.MaxAsync(a => (long?)a.Stream, cancellationToken),
            await db.Receipts.MaxAsync(r => (long?)r.Stream, cancellationToken),
            await db.DeviceListChanges.MaxAsync(c => (long?)c.Stream, cancellationToken),
            await db.ToDevice.MaxAsync(m => (long?)m.Stream, cancellationToken),
        }.Max() ?? 0;
        Interlocked.CompareExchange(ref current, stored, -1);
        // Return position
        return Volatile.Read(ref current);
    }

    /// <summary>Take a position for a change that is not stored, such as typing, so waiting syncs see it.</summary>
    public Task<long> AdvanceAsync(CancellationToken cancellationToken) => WriteAsync(Task.FromResult, cancellationToken);

    /// <summary>Run <paramref name="write"/> with the next position, alone; the position becomes current when it returns.</summary>
    /// <remarks>Throwing leaves the position unused; the next write takes it again.</remarks>
    public async Task<T> WriteAsync<T>(Func<long, Task<T>> write, CancellationToken cancellationToken)
    {
        // One write at a time
        await gate.WaitAsync(cancellationToken);
        T result;
        long position;
        try
        {
            position = await GetCurrentAsync(cancellationToken) + 1;
            result = await write(position);
            await StoreAsync(position, cancellationToken);
            Volatile.Write(ref current, position);
        }
        finally
        {
            gate.Release();
        }

        // Wake waiting syncs
        Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        // Return result
        return result;
    }

    /// <summary>Wait until the stream moves past <paramref name="since"/>, <paramref name="timeout"/> passes or <paramref name="endEarly"/> is cancelled; return the current position.</summary>
    /// <param name="since">Position the caller has.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait with an exception.</param>
    /// <param name="endEarly">Ends the wait as the timeout does, without an exception, e.g. <see cref="Stopping"/>.</param>
    public async Task<long> WaitPastAsync(long since, TimeSpan timeout, CancellationToken cancellationToken, CancellationToken endEarly = default)
    {
        // Already past, or wait for the next write
        Task next = Volatile.Read(ref changed).Task;
        long position = await GetCurrentAsync(cancellationToken);
        if (position > since || timeout <= TimeSpan.Zero || endEarly.IsCancellationRequested) return position;
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, endEarly);
        await Task.WhenAny(next, Task.Delay(timeout, wait.Token));
        cancellationToken.ThrowIfCancellationRequested();
        // Return position
        return await GetCurrentAsync(cancellationToken);
    }

    /// <summary>Store the position, so positions handed out for unstored changes are not reused after a restart.</summary>
    private async Task StoreAsync(long position, CancellationToken cancellationToken)
    {
        // Insert or update the counter row
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CounterRow? row = await db.Counters.SingleOrDefaultAsync(c => c.Name == CounterName, cancellationToken);
        if (row is null) db.Counters.Add(new CounterRow { Name = CounterName, Value = position });
        else row.Value = position;
        await db.SaveChangesAsync(cancellationToken);
    }
}
