namespace Uniform.Matrix.Server.Rooms;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Accounts;

/// <summary><see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4140">Delayed events</see> (MSC4140): events sent later unless restarted or cancelled first, which MatrixRTC uses to end the call membership of a client that disappears.</summary>
/// <remarks>Kept in memory, so a restart forgets them; their IDs are random enough to act as the secret that restarting, cancelling and sending need.</remarks>
public sealed class DelayedEvents : IDisposable
{
    private const int MaxPerUser = 100;
    private readonly RoomService rooms;
    private readonly IOptionsMonitor<CallOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<DelayedEvents> logger;
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>A scheduled event.</summary>
    private sealed record Pending(string DelayId, Requester Requester, string RoomId, string Type, string? StateKey, JsonObject Content, TimeSpan Delay)
    {
        /// <summary>Timer that sends it.</summary>
        public ITimer? Timer { get; set; }
        /// <summary>When the delay last started.</summary>
        public DateTimeOffset RunningSince { get; set; }
    }

    /// <summary>Create the service.</summary>
    public DelayedEvents(RoomService rooms, IOptionsMonitor<CallOptions> options, TimeProvider time, ILogger<DelayedEvents> logger)
    {
        // Store dependencies
        this.rooms = rooms;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Schedule a message (<paramref name="stateKey"/> null) or state event; return its delay ID.</summary>
    public string Schedule(Requester requester, string roomId, string type, string? stateKey, JsonObject content, long delayMilliseconds)
    {
        // Assert a delay within the limit, and few enough per user
        TimeSpan delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        if (delayMilliseconds < 0 || delay > options.CurrentValue.MaxEventDelay) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"The delay must be between 0 and {(long)options.CurrentValue.MaxEventDelay.TotalMilliseconds} ms.");
        if (pending.Values.Count(p => p.Requester.UserId == requester.UserId) >= MaxPerUser) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.LimitExceeded, "Too many delayed events.");

        // Timer from now
        string delayId = "syd_" + Base64.EncodeUrlSafe(RandomNumberGenerator.GetBytes(18));
        Pending entry = new(delayId, requester, roomId, type, stateKey, content, delay) { RunningSince = time.GetUtcNow() };
        pending[delayId] = entry;
        entry.Timer = time.CreateTimer(_ => _ = SendAsync(delayId), null, delay, Timeout.InfiniteTimeSpan);
        // Return ID
        return delayId;
    }

    /// <summary>Restart, cancel or send now (<paramref name="action"/> <c>restart</c>, <c>cancel</c> or <c>send</c>) a delayed event; throw <c>M_NOT_FOUND</c> when there is none, e.g. as it was sent already.</summary>
    public async Task ActAsync(string delayId, string action, CancellationToken cancellationToken)
    {
        // Assert a pending event and a known action
        if (!pending.TryGetValue(delayId, out Pending? entry)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "No such delayed event.");
        switch (action)
        {
            case "restart":
                entry.RunningSince = time.GetUtcNow();
                entry.Timer?.Change(entry.Delay, Timeout.InfiniteTimeSpan);
                return;
            case "cancel":
                if (pending.TryRemove(delayId, out _)) entry.Timer?.Dispose();
                return;
            case "send":
                await SendAsync(delayId);
                return;
            default:
                throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown action {action}.");
        }
    }

    /// <summary>Return the user's delayed events as MSC4140 lists them.</summary>
    public JsonArray List(string userId) => new([.. pending.Values.Where(p => p.Requester.UserId == userId).Select(p =>
    {
        JsonObject item = new() { ["delay_id"] = p.DelayId, ["room_id"] = p.RoomId, ["type"] = p.Type, ["delay"] = (long)p.Delay.TotalMilliseconds, ["running_since"] = p.RunningSince.ToUnixTimeMilliseconds(), ["content"] = p.Content.DeepClone() };
        if (p.StateKey is not null) item["state_key"] = p.StateKey;
        return (JsonNode)item;
    })]);

    /// <summary>Send a pending event once; log failures, as there is no one to tell.</summary>
    private async Task SendAsync(string delayId)
    {
        // Only once, also when the timer and a send request meet
        if (!pending.TryRemove(delayId, out Pending? entry)) return;
        entry.Timer?.Dispose();
        try
        {
            if (entry.StateKey is null) await rooms.SendMessageAsync(entry.Requester, entry.RoomId, entry.Type, entry.Content, delayId, CancellationToken.None);
            else await rooms.SetStateAsync(entry.Requester, entry.RoomId, entry.Type, entry.StateKey, entry.Content, CancellationToken.None);
        }
        catch (MatrixException ex)
        {
            // On error log
            logger.LogInformation("Delayed event of {UserId} in {RoomId} not sent: {Error}", entry.Requester.UserId, entry.RoomId, ex.Message);
        }
    }

    /// <summary>Stop all timers.</summary>
    public void Dispose()
    {
        // Pending events are dropped
        foreach (Pending entry in pending.Values) entry.Timer?.Dispose();
        pending.Clear();
    }
}
