namespace Uniform.Matrix.Server.Federation;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;

/// <summary>Outgoing <see href="https://spec.matrix.org/latest/server-server-api/#transactions">transactions</see>, one queue per destination server: PDUs and EDUs are batched, and retried with growing pauses while the server cannot be reached.</summary>
/// <remarks>Queues are kept in memory, so a restart drops what was not sent yet. A refusal of the other server, other than <c>429</c>, drops the batch, as sending it again would be refused again. Each queue is bounded; when full, its oldest EDUs and then PDUs go first.</remarks>
public sealed class FederationQueue : IDisposable
{
    /// <summary>Most PDUs in one transaction, as the spec allows.</summary>
    public const int MaxPdus = 50;
    /// <summary>Most EDUs in one transaction, as the spec allows.</summary>
    public const int MaxEdus = 100;
    /// <summary>Pause before the first retry; it doubles after each failure.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    /// <summary>Longest pause between retries.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    private const int MaxQueued = 5000;
    private readonly FederationClient federation;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly TimeProvider time;
    private readonly ILogger<FederationQueue> logger;
    private readonly ConcurrentDictionary<string, Destination> destinations = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly string transactionPrefix;
    private long transactionCounter;

    /// <summary>What waits for one server.</summary>
    private sealed class Destination
    {
        /// <summary>Guards the lists and <see cref="Running"/>.</summary>
        public Lock Gate { get; } = new();
        /// <summary>Waiting PDUs, oldest first.</summary>
        public List<JsonObject> Pdus { get; } = [];
        /// <summary>Waiting EDUs, oldest first.</summary>
        public List<JsonObject> Edus { get; } = [];
        /// <summary>Whether a sending loop runs for the server.</summary>
        public bool Running { get; set; }
    }

    /// <summary>Create the queues.</summary>
    public FederationQueue(FederationClient federation, IOptionsMonitor<ServerOptions> server, TimeProvider time, ILogger<FederationQueue> logger)
    {
        // Store dependencies; transaction IDs stay unique across restarts by their start time
        this.federation = federation;
        this.server = server;
        this.time = time;
        this.logger = logger;
        transactionPrefix = time.GetUtcNow().ToUnixTimeMilliseconds().ToString();
    }

    /// <summary>Queue a PDU for <paramref name="destination"/>.</summary>
    public void EnqueuePdu(string destination, JsonObject pdu) => Enqueue(destination, pdu, null);

    /// <summary>Queue an EDU, e.g. <c>{"edu_type": "m.typing", "content": {...}}</c>, for <paramref name="destination"/>.</summary>
    public void EnqueueEdu(string destination, JsonObject edu) => Enqueue(destination, null, edu);

    /// <summary>Return how many PDUs and EDUs wait for <paramref name="destination"/>.</summary>
    public (int Pdus, int Edus) GetQueued(string destination)
    {
        // Nothing for unknown servers
        if (!destinations.TryGetValue(destination, out Destination? queue)) return (0, 0);
        lock (queue.Gate)
        {
            // Return counts
            return (queue.Pdus.Count, queue.Edus.Count);
        }
    }

    /// <summary>Add to a queue and start its sending loop unless it runs.</summary>
    private void Enqueue(string destination, JsonObject? pdu, JsonObject? edu)
    {
        // Bounded: drop the oldest EDUs first, as they matter least later
        Destination queue = destinations.GetOrAdd(destination, _ => new Destination());
        lock (queue.Gate)
        {
            if (pdu is not null) queue.Pdus.Add(pdu.DeepClone().AsObject());
            if (edu is not null) queue.Edus.Add(edu.DeepClone().AsObject());
            while (queue.Pdus.Count + queue.Edus.Count > MaxQueued) (queue.Edus.Count > 0 ? queue.Edus : queue.Pdus).RemoveAt(0);
            if (queue.Running) return;
            queue.Running = true;
        }
        // Done
        _ = Task.Run(() => RunAsync(destination, queue));
    }

    /// <summary>Send batches until the queue is empty, pausing longer after each failure.</summary>
    private async Task RunAsync(string destination, Destination queue)
    {
        // Batch by batch
        TimeSpan pause = FirstRetry;
        while (!stopping.IsCancellationRequested)
        {
            List<JsonObject> pdus, edus;
            lock (queue.Gate)
            {
                if (queue.Pdus.Count == 0 && queue.Edus.Count == 0)
                {
                    queue.Running = false;
                    return;
                }
                pdus = [.. queue.Pdus.Take(MaxPdus)];
                edus = [.. queue.Edus.Take(MaxEdus)];
            }

            // Done with the batch when sent or refused for good; otherwise wait and try again
            if (await TrySendAsync(destination, pdus, edus))
            {
                lock (queue.Gate)
                {
                    queue.Pdus.RemoveRange(0, Math.Min(pdus.Count, queue.Pdus.Count));
                    queue.Edus.RemoveRange(0, Math.Min(edus.Count, queue.Edus.Count));
                }
                pause = FirstRetry;
                continue;
            }
            try
            {
                await Task.Delay(pause, time, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            pause = pause * 2 > MaxRetry ? MaxRetry : pause * 2;
        }
    }

    /// <summary>Send one transaction; return whether the batch is done with, sent or refused for good.</summary>
    private async Task<bool> TrySendAsync(string destination, List<JsonObject> pdus, List<JsonObject> edus)
    {
        // The transaction, under an ID of its own
        string transactionId = $"{transactionPrefix}_{Interlocked.Increment(ref transactionCounter)}";
        JsonObject transaction = new() { ["origin"] = server.CurrentValue.ServerName, ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["pdus"] = new JsonArray([.. pdus.Select(p => (JsonNode)p.DeepClone())]), ["edus"] = new JsonArray([.. edus.Select(e => (JsonNode)e.DeepClone())]) };
        try
        {
            JsonNode? answer = await federation.SendAsync(HttpMethod.Put, destination, $"/_matrix/federation/v1/send/{transactionId}", transaction, stopping.Token);
            foreach ((string eventId, JsonNode? result) in answer?["pdus"] as JsonObject ?? [])
            {
                if (result?["error"]?.GetValue<string>() is string error) logger.LogWarning("{Destination} rejected event {EventId}: {Error}", destination, eventId, error);
            }
            return true;
        }
        catch (MatrixException ex) when ((int)ex.StatusCode is >= 400 and < 500 && ex.StatusCode != HttpStatusCode.TooManyRequests)
        {
            logger.LogInformation("{Destination} refused a transaction of {Pdus} events and {Edus} EDUs: {Error}", destination, pdus.Count, edus.Count, ex.Message);
            return true;
        }
        catch (MatrixException ex)
        {
            // On error retry later
            logger.LogInformation("Sending to {Destination} failed, retrying later: {Error}", destination, ex.Message);
            return false;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Stop the sending loops; what waits is dropped.</summary>
    public void Dispose()
    {
        // Loops end at their next step
        stopping.Cancel();
        stopping.Dispose();
    }
}
