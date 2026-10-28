namespace Uniform.Matrix.Protocol.Client;

using System.Net;
using System.Runtime.CompilerServices;
using Uniform.Matrix.Protocol.Core;

// Sync, sync loop and filters.
public sealed partial class MatrixClient
{
    /// <summary>First wait after a failed sync in <see cref="SyncLoopAsync"/>; doubles on each failure.</summary>
    public TimeSpan SyncRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Longest wait between failed syncs in <see cref="SyncLoopAsync"/>.</summary>
    public TimeSpan SyncRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3sync">Syncs</see> once; waits up to <paramref name="timeout"/> for new events.</summary>
    /// <param name="since">Previous <see cref="SyncResponse.NextBatch"/>; null for an initial sync.</param>
    /// <param name="timeout">Long-poll time; null returns immediately.</param>
    /// <param name="filter">Filter ID from <see cref="CreateFilterAsync"/> or inline JSON from <see cref="Filter.ToJson"/>.</param>
    /// <param name="fullState">Whether to return all state instead of changes since <paramref name="since"/>.</param>
    /// <param name="setPresence">Presence to set while syncing, see <see cref="PresenceStates"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SyncResponse> SyncAsync(string? since = null, TimeSpan? timeout = null, string? filter = null, bool fullState = false, string? setPresence = null, CancellationToken cancellationToken = default)
    {
        // Build query
        string path = WithQuery("_matrix/client/v3/sync",
        [
            ("since", since),
            ("timeout", timeout is TimeSpan t ? ((long)t.TotalMilliseconds).ToString() : null),
            ("filter", filter),
            ("full_state", fullState ? "true" : null),
            ("set_presence", setPresence),
        ]);
        // Return response
        return SendAsync<SyncResponse>(HttpMethod.Get, path, body: null, authenticated: true, cancellationToken);
    }

    /// <summary>Sync repeatedly, yielding each response; retry transient failures with backoff and throw on others such as <see cref="ErrorCodes.UnknownToken"/>.</summary>
    /// <param name="since">Token to resume from; null for an initial sync.</param>
    /// <param name="filter">Filter ID or inline JSON, see <see cref="SyncAsync"/>.</param>
    /// <param name="timeout">Long-poll time per request; 30 seconds when null.</param>
    /// <param name="onRetry">Called with the error and wait time before each retry, e.g. to show a connection warning.</param>
    /// <param name="cancellationToken">Cancellation token; ends the loop.</param>
    public async IAsyncEnumerable<SyncResponse> SyncLoopAsync(string? since = null, string? filter = null, TimeSpan? timeout = null, Action<Exception, TimeSpan>? onRetry = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Initialize polling
        TimeSpan pollTimeout = timeout ?? TimeSpan.FromSeconds(30);
        TimeSpan delay = SyncRetryDelay;

        // Sync until cancelled
        while (true)
        {
            (SyncResponse? response, Exception? error) = await TrySyncAsync(since, pollTimeout, filter, cancellationToken);
            if (response is null)
            {
                onRetry?.Invoke(error!, delay);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, SyncRetryMaxDelay.Ticks));
                continue;
            }

            delay = SyncRetryDelay;
            since = response.NextBatch;
            yield return response;
        }
    }

    /// <summary>Run <paramref name="action"/> until it succeeds, retrying transient failures (network errors, 5xx, rate limiting) with the backoff of <see cref="SyncLoopAsync"/>; throw other errors.</summary>
    /// <param name="action">The work, e.g. a first sync.</param>
    /// <param name="onRetry">Called with the error and wait time before each retry.</param>
    /// <param name="cancellationToken">Cancellation token; ends the retries.</param>
    public async Task<T> RetryTransientAsync<T>(Func<CancellationToken, Task<T>> action, Action<Exception, TimeSpan>? onRetry, CancellationToken cancellationToken)
    {
        // Until it succeeds or fails for good
        TimeSpan delay = SyncRetryDelay;
        while (true)
        {
            try
            {
                // Return result
                return await action(cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                onRetry?.Invoke(ex, delay);
            }
            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, SyncRetryMaxDelay.Ticks));
        }
    }

    /// <summary>Sync once, returning transient errors instead of throwing them.</summary>
    private async Task<(SyncResponse? Response, Exception? Error)> TrySyncAsync(string? since, TimeSpan timeout, string? filter, CancellationToken cancellationToken)
    {
        // Return response or transient error
        try
        {
            return (await SyncAsync(since, timeout, filter, cancellationToken: cancellationToken), null);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return (null, ex);
        }
    }

    /// <summary>Whether <paramref name="exception"/> is worth retrying: network errors, 5xx, rate limiting and HTTP timeouts.</summary>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        MatrixException m => m.StatusCode >= HttpStatusCode.InternalServerError || m.Errcode == ErrorCodes.LimitExceeded,
        HttpRequestException h => h.StatusCode is null || h.StatusCode >= HttpStatusCode.InternalServerError,
        // HttpClient timeout, not caller cancellation
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3useruseridfilter">Uploads</see> <paramref name="filter"/> and returns its ID for <see cref="SyncAsync"/>.</summary>
    public async Task<string> CreateFilterAsync(Filter filter, CancellationToken cancellationToken = default)
    {
        // Upload filter
        FilterIdResponse response = await SendAsync<FilterIdResponse>(HttpMethod.Post, $"_matrix/client/v3/user/{Escape(RequireSession().UserId)}/filter", filter, authenticated: true, cancellationToken);
        // Return filter ID
        return response.FilterId;
    }
}
