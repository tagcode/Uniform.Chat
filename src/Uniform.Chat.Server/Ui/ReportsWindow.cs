namespace Uniform.Chat.Server.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Server.Rooms;
using global::Terminal.Gui.App;

/// <summary>Users' reports of events, rooms and users, newest first; reports can be resolved, opened again or deleted, and resolved ones shown or hidden.</summary>
public sealed class ReportsWindow : AdminWindow<Report>
{
    private readonly ReportService reports;
    private bool showResolved;

    /// <summary>Build the window and load the open reports.</summary>
    public ReportsWindow(IApplication app, ReportService reports)
        : base(app, "Reports", $"{"Received",-16} {"Reporter",-28} {"About",-28} Reason")
    {
        // Actions, then the list
        this.reports = reports;
        AddAction("_Resolve", ToggleResolved);
        AddAction("_Delete", Delete);
        AddAction("_Show resolved", () => { showResolved = !showResolved; Reload(); });
        Reload();
    }

    /// <summary>Load the reports, resolved ones when shown.</summary>
    protected override Task<IReadOnlyList<Report>> LoadAsync() => reports.GetAsync(showResolved, CancellationToken.None);

    /// <summary>One report per row.</summary>
    protected override string FormatRow(Report report) => $"{report.Received.ToLocalTime():yyyy-MM-dd HH:mm} {Clip(report.Reporter, 28),-28} {Clip(Target(report), 28),-28} {(report.Resolved ? "[resolved] " : "")}{Clip(report.Reason, 60)}";

    /// <summary>The whole reason, what was reported and the reported event's text.</summary>
    protected override string Describe(Report report) =>
        string.Join('\n', new[]
        {
            $"Report {report.Id} by {report.Reporter} at {report.Received.ToLocalTime():yyyy-MM-dd HH:mm}{(report.Resolved ? ", resolved" : "")}.",
            report.RoomId is null ? null : $"Room: {report.RoomId}",
            report.EventId is null ? null : $"Event: {report.EventId}{(report.EventSummary is null ? " (not on this server)" : $" — {report.EventSummary}")}",
            report.UserId is null ? null : $"User: {report.UserId}",
            $"Reason: {(report.Reason.Length == 0 ? "none given" : report.Reason)}",
        }.OfType<string>());

    /// <summary>Resolve the selected report, or open it again.</summary>
    private void ToggleResolved()
    {
        // Flip the flag
        if (Selected is not Report report) return;
        Run("Resolve", () => reports.SetResolvedAsync(report.Id, !report.Resolved, CancellationToken.None));
        Reload();
    }

    /// <summary>Delete the selected report after confirmation.</summary>
    private void Delete()
    {
        // Confirm, then delete
        if (Selected is not Report report || !Dialogs.Confirm(Tui, "Delete", $"Delete report {report.Id} by {report.Reporter}? Resolving keeps it for later.")) return;
        Run("Delete", () => reports.DeleteAsync(report.Id, CancellationToken.None));
        Reload();
    }

    /// <summary>Return what a report is about: the user, the event or the room.</summary>
    private static string Target(Report report) => report.UserId ?? report.EventId ?? report.RoomId ?? "";
}
