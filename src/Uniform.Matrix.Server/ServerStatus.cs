namespace Uniform.Matrix.Server;

/// <summary>Live counters of the running server, for the dashboard.</summary>
public sealed class ServerStatus
{
    private long requests;
    private long failedRequests;
    private int activeRequests;

    /// <summary>Create counters starting now.</summary>
    public ServerStatus(TimeProvider time)
    {
        // Start time
        Started = time.GetUtcNow();
    }

    /// <summary>When the server started.</summary>
    public DateTimeOffset Started { get; }
    /// <summary>Requests answered.</summary>
    public long Requests => Interlocked.Read(ref requests);
    /// <summary>Requests answered with a 5xx status.</summary>
    public long FailedRequests => Interlocked.Read(ref failedRequests);
    /// <summary>Requests in progress, including waiting syncs.</summary>
    public int ActiveRequests => Volatile.Read(ref activeRequests);

    /// <summary>Count a request that started.</summary>
    internal void RequestStarted() => Interlocked.Increment(ref activeRequests);

    /// <summary>Count a request that finished with <paramref name="statusCode"/>.</summary>
    internal void RequestFinished(int statusCode)
    {
        // Totals and in-progress
        Interlocked.Decrement(ref activeRequests);
        Interlocked.Increment(ref requests);
        if (statusCode >= 500) Interlocked.Increment(ref failedRequests);
    }
}
