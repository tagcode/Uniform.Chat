namespace Uniform.Matrix.Server.Rooms;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

/// <summary>A report as the admin screens list it.</summary>
public sealed record Report
{
    /// <summary>Report ID.</summary>
    public long Id { get; init; }
    /// <summary>Who reported.</summary>
    public required string Reporter { get; init; }
    /// <summary>Reported room, or the room of the reported event.</summary>
    public string? RoomId { get; init; }
    /// <summary>Reported event, if any.</summary>
    public string? EventId { get; init; }
    /// <summary>Reported user, if any.</summary>
    public string? UserId { get; init; }
    /// <summary>The reporter's reason.</summary>
    public required string Reason { get; init; }
    /// <summary>When it was reported.</summary>
    public DateTimeOffset Received { get; init; }
    /// <summary>Whether the admin has dealt with it.</summary>
    public bool Resolved { get; init; }
    /// <summary>The reported event's sender, type and text, when the event is here; encrypted text cannot be read.</summary>
    public string? EventSummary { get; init; }
}

/// <summary>Users' <see href="https://spec.matrix.org/latest/client-server-api/#reporting-content">reports</see> of events, rooms and users: kept for the admin to review and resolve, and logged.</summary>
public sealed class ReportService
{
    /// <summary>Longest reason kept.</summary>
    public const int MaxReason = 1000;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly TimeProvider time;
    private readonly ILogger<ReportService> logger;

    /// <summary>Create the service.</summary>
    public ReportService(IDbContextFactory<ServerDbContext> contexts, TimeProvider time, ILogger<ReportService> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Keep and log a report.</summary>
    /// <param name="reporter">Who reports.</param>
    /// <param name="roomId">Reported room, or the room of the reported event.</param>
    /// <param name="eventId">Reported event, if any.</param>
    /// <param name="userId">Reported user, if any.</param>
    /// <param name="reason">Why; cut to <see cref="MaxReason"/> characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddAsync(string reporter, string? roomId, string? eventId, string? userId, string reason, CancellationToken cancellationToken)
    {
        // Stored, then logged for those reading the log
        string kept = reason.Length > MaxReason ? reason[..MaxReason] : reason;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Reports.Add(new ReportRow { Reporter = reporter, RoomId = roomId, EventId = eventId, UserId = userId, Reason = kept, ReceivedTs = time.GetUtcNow().ToUnixTimeMilliseconds() });
        await db.SaveChangesAsync(cancellationToken);
        // Done
        logger.LogWarning("Report by {Reporter}: {Target}: {Reason}", reporter, string.Join(" ", new[] { roomId, eventId, userId }.OfType<string>()), kept);
    }

    /// <summary>Return the reports, newest first; resolved ones only when asked.</summary>
    public async Task<IReadOnlyList<Report>> GetAsync(bool includeResolved, CancellationToken cancellationToken)
    {
        // Reports and the events they name
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<ReportRow> rows = await db.Reports.AsNoTracking().Where(r => includeResolved || !r.Resolved).OrderByDescending(r => r.Id).ToListAsync(cancellationToken);
        List<string> eventIds = [.. rows.Select(r => r.EventId).OfType<string>().Distinct()];
        Dictionary<string, EventRow> events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.EventId)).ToDictionaryAsync(e => e.EventId, cancellationToken);
        // Return reports
        return [.. rows.Select(r => new Report
        {
            Id = r.Id,
            Reporter = r.Reporter,
            RoomId = r.RoomId,
            EventId = r.EventId,
            UserId = r.UserId,
            Reason = r.Reason,
            Received = DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedTs),
            Resolved = r.Resolved,
            EventSummary = r.EventId is string id && events.TryGetValue(id, out EventRow? row) ? Summarize(row) : null,
        })];
    }

    /// <summary>Return the number of reports not resolved.</summary>
    public async Task<int> CountOpenAsync(CancellationToken cancellationToken)
    {
        // Open ones
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return count
        return await db.Reports.CountAsync(r => !r.Resolved, cancellationToken);
    }

    /// <summary>Mark a report resolved, or open again.</summary>
    public async Task SetResolvedAsync(long id, bool resolved, CancellationToken cancellationToken)
    {
        // Flag of the report
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Reports.Where(r => r.Id == id).ExecuteUpdateAsync(r => r.SetProperty(x => x.Resolved, resolved), cancellationToken) == 0) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown report.");
        // Done
        logger.LogInformation(resolved ? "Admin resolved report {Id}" : "Admin opened report {Id} again", id);
    }

    /// <summary>Delete a report.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        // Gone for good
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Reports.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) == 0) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown report.");
        // Done
        logger.LogInformation("Admin deleted report {Id}", id);
    }

    /// <summary>Return an event's sender, type and text, if it has readable text.</summary>
    private static string Summarize(EventRow row)
    {
        // Text of unencrypted messages
        string? body = JsonNode.Parse(row.Json)?["content"]?["body"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        string what = row.Type == EventTypes.RoomEncrypted ? "an encrypted message, which the server cannot read" : row.RedactedBy is not null ? $"{row.Type}, since deleted" : body is null ? row.Type : $"\"{(body.Length > 300 ? body[..300] + "…" : body)}\"";
        // Return summary
        return $"{row.Sender}: {what}";
    }
}
