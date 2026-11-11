namespace Uniform.LiveKit.Authorization;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Uniform.LiveKit;

/// <summary>Keeps the delayed leaves (MSC4140) that members handed over: while a member is on LiveKit, its delayed leave is restarted before it goes off; once LiveKit no longer has the member, the leave is sent at once, so the member's call membership ends.</summary>
/// <remarks>
/// A job waits for the member to appear in its LiveKit room, up to the delay, then looks every <see cref="CheckInterval"/> whether it is still there; it restarts the delayed event at four fifths of its delay, as lk-jwt-service does. A job of the same member in the same room replaces the earlier one. Jobs live in memory: after a restart of the service, the homeserver sends a member's leave when its delay runs out.
/// </remarks>
/// <param name="options">The current settings, for LiveKit's URL and keys.</param>
/// <param name="events">Acts on the delayed events at the members' homeservers.</param>
/// <param name="http">The client of LiveKit's room service.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">Logger of the jobs.</param>
public sealed class DelayedLeaveKeeper(Func<LiveKitAuthorizerOptions> options, IDelayedEvents events, HttpClient http, TimeProvider time, ILogger<DelayedLeaveKeeper> logger) : IAsyncDisposable
{
    /// <summary>How often a job looks whether its member is still on LiveKit.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    /// <summary>The longest delay taken, as lk-jwt-service takes.</summary>
    public static readonly TimeSpan MostDelay = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<(string Room, string Identity), Job> jobs = new();
    private readonly CancellationTokenSource stopping = new();

    /// <summary>The members whose leaves are kept now, by LiveKit room and identity.</summary>
    public IReadOnlyCollection<(string Room, string Identity)> Members => [.. jobs.Keys];

    /// <summary>Keep the delayed leave <paramref name="delayId"/> of a user of <paramref name="serverName"/>, whose participant is <paramref name="identity"/> in LiveKit room <paramref name="room"/>, with a delay of <paramref name="delay"/>.</summary>
    public void Keep(string serverName, string delayId, TimeSpan delay, string room, string identity)
    {
        // A new job, replacing the member's earlier one
        Job job = new(serverName, delayId, delay < MostDelay ? delay : MostDelay, room, identity, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
        if (jobs.TryGetValue((room, identity), out Job? earlier)) earlier.Stopping.Cancel();
        jobs[(room, identity)] = job;
        logger.LogInformation("Keeping delayed leave of {Identity} in {Room} on {Server}, delay {Delay}", identity, room, serverName, job.Delay);
        // Done
        job.Running = RunAsync(job);
    }

    /// <summary>Stop all jobs, leaving the delayed events to the homeservers.</summary>
    public async ValueTask DisposeAsync()
    {
        // Each job ends
        await stopping.CancelAsync();
        foreach (Job job in jobs.Values)
        {
            if (job.Running is not null) await job.Running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // Done
        stopping.Dispose();
    }

    /// <summary>Run a job: wait for the member, keep the leave while it stays, send it when it goes.</summary>
    private async Task RunAsync(Job job)
    {
        // Until it is replaced, ends, or the service stops
        CancellationToken cancellationToken = job.Stopping.Token;
        try
        {
            if (!await WaitForMemberAsync(job, cancellationToken)) return;
            DateTimeOffset restartAt = time.GetUtcNow() + job.Delay * 4 / 5;
            while (true)
            {
                await Task.Delay(CheckInterval, time, cancellationToken);
                if (await IsPresentAsync(job, cancellationToken) == false)
                {
                    logger.LogInformation("{Identity} left LiveKit room {Room}; sending its delayed leave", job.Identity, job.Room);
                    await UpdateAsync(job, "send", cancellationToken);
                    return;
                }
                if (time.GetUtcNow() < restartAt) continue;
                if (!await UpdateAsync(job, "restart", cancellationToken)) return;
                restartAt = time.GetUtcNow() + job.Delay * 4 / 5;
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced, or the service stops
        }
        catch (Exception e)
        {
            // On a failure the homeserver's delay still ends the membership
            logger.LogWarning(e, "Keeping delayed leave of {Identity} in {Room} failed", job.Identity, job.Room);
        }
        finally
        {
            ((ICollection<KeyValuePair<(string, string), Job>>)jobs).Remove(new((job.Room, job.Identity), job));
            job.Stopping.Dispose();
        }
    }

    /// <summary>Return true once the member is on LiveKit, restarting its delayed leave meanwhile; false when it never came within the delay, or the leave is gone.</summary>
    private async Task<bool> WaitForMemberAsync(Job job, CancellationToken cancellationToken)
    {
        // Looked for at each check
        DateTimeOffset until = time.GetUtcNow() + job.Delay;
        DateTimeOffset restartAt = time.GetUtcNow() + job.Delay * 4 / 5;
        while (time.GetUtcNow() < until)
        {
            if (await IsPresentAsync(job, cancellationToken) == true) return true;
            if (time.GetUtcNow() >= restartAt)
            {
                if (!await UpdateAsync(job, "restart", cancellationToken)) return false;
                restartAt = time.GetUtcNow() + job.Delay * 4 / 5;
            }
            await Task.Delay(CheckInterval, time, cancellationToken);
        }
        // On a member that never came
        logger.LogInformation("{Identity} never joined LiveKit room {Room}; its delayed leave goes off by itself", job.Identity, job.Room);
        return false;
    }

    /// <summary>Return whether the member is in its LiveKit room; null when LiveKit could not be asked, which keeps the leave as it is.</summary>
    private async Task<bool?> IsPresentAsync(Job job, CancellationToken cancellationToken)
    {
        // LiveKit's room service with the current keys
        LiveKitAuthorizerOptions settings = options();
        try
        {
            return await new LiveKitRoomService(http, settings.Url, settings.ApiKey, settings.ApiSecret, time).HasParticipantAsync(job.Room, job.Identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // On LiveKit not answering: no verdict
            logger.LogDebug(e, "Asking LiveKit for {Identity} failed", job.Identity);
            return null;
        }
    }

    /// <summary>Restart or send the job's delayed event; return false when the homeserver no longer has it. A homeserver that cannot be reached counts as having it, so the job tries again.</summary>
    private async Task<bool> UpdateAsync(Job job, string action, CancellationToken cancellationToken)
    {
        // At the member's homeserver
        try
        {
            bool found = await events.UpdateAsync(job.ServerName, job.DelayId, action, cancellationToken).ConfigureAwait(false);
            if (!found) logger.LogInformation("Delayed leave of {Identity} is gone from {Server}", job.Identity, job.ServerName);
            return found;
        }
        catch (HttpRequestException e)
        {
            // Done: again at the next check
            logger.LogInformation("Delayed leave of {Identity} not {Action}: {Error}", job.Identity, action, e.Message);
            return true;
        }
    }

    /// <summary>A kept delayed leave.</summary>
    /// <param name="ServerName">The member's homeserver.</param>
    /// <param name="DelayId">The delayed event's ID.</param>
    /// <param name="Delay">Its delay.</param>
    /// <param name="Room">The LiveKit room.</param>
    /// <param name="Identity">The member's participant identity.</param>
    /// <param name="Stopping">Ends the job.</param>
    private sealed record Job(string ServerName, string DelayId, TimeSpan Delay, string Room, string Identity, CancellationTokenSource Stopping)
    {
        /// <summary>The job's task.</summary>
        public Task? Running { get; set; }
    }
}
