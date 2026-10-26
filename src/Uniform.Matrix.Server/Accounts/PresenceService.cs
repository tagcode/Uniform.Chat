namespace Uniform.Matrix.Server.Accounts;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Minimal <see href="https://spec.matrix.org/latest/client-server-api/#presence">presence</see>: the status users set, kept in memory and not sent in sync yet.</summary>
public sealed class PresenceService
{
    private static readonly string[] States = ["online", "offline", "unavailable"];
    private readonly ConcurrentDictionary<string, (string Presence, string? Status, DateTimeOffset Changed)> users = new();
    private readonly TimeProvider time;

    /// <summary>Create the service.</summary>
    public PresenceService(TimeProvider time)
    {
        // Store dependency
        this.time = time;
    }

    /// <summary>Set a user's presence and optional status message.</summary>
    public void Set(string userId, string presence, string? status)
    {
        // Assert known state
        if (!States.Contains(presence)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown presence {presence}.");
        users[userId] = (presence, status, time.GetUtcNow());
    }

    /// <summary>Return a user's presence; <c>offline</c> when never set.</summary>
    public JsonObject Get(string userId)
    {
        // Offline by default
        if (!users.TryGetValue(userId, out (string Presence, string? Status, DateTimeOffset Changed) entry)) return new JsonObject { ["presence"] = "offline" };
        JsonObject result = new() { ["presence"] = entry.Presence, ["last_active_ago"] = (long)(time.GetUtcNow() - entry.Changed).TotalMilliseconds };
        if (entry.Status is not null) result["status_msg"] = entry.Status;
        // Return presence
        return result;
    }
}
