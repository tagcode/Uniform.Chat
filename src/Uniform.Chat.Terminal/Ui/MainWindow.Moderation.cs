namespace Uniform.Chat.Terminal.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

// Reporting messages, users and rooms, blocking users, and deleting the account.
public sealed partial class MainWindow
{
    /// <summary>Reasons offered when reporting, with hotkeys.</summary>
    private static readonly string[] ReportReasons = ["_Spam", "_Harassment or hate", "Se_xual or violent content", "_Illegal content", "_Other"];

    /// <summary>Ask why <paramref name="what"/> is reported; return the reason without its hotkey, or null when cancelled.</summary>
    private string? AskReportReason(string what)
    {
        // A reason from the list
        int? choice = Dialogs.Choose(app, "Report", $"Report {what} to the admins of your server. Why?", [.. ReportReasons, "_Cancel"]);
        // Return reason
        return choice is int index && index < ReportReasons.Length ? ReportReasons[index].Replace("_", "") : null;
    }

    /// <summary>Report <paramref name="clientEvent"/> of <paramref name="roomId"/>, asking why.</summary>
    private void ReportMessage(string roomId, ClientEvent clientEvent)
    {
        // Reason, then the report
        if (clientEvent.EventId is not string eventId || AskReportReason("this message") is not string reason) return;
        _ = RunSafelyAsync("Reporting the message", async client =>
        {
            await client.ReportEventAsync(roomId, eventId, reason);
            app.Invoke(() => ShowStatus("Reported the message to the admins of your server. /ignore blocks its sender."));
        });
    }

    /// <summary>Report the user of <paramref name="argument"/>, or the open room when it is empty, asking why.</summary>
    private void Report(string argument)
    {
        // A user ID, else the open room
        string target = argument.Trim();
        if (target.Length > 0 && !UserId.TryParse(target, out _))
        {
            ShowStatus($"\"{target}\" is not a user ID; /report @user:server, or /report alone for this room.");
            return;
        }
        if (target.Length == 0 && currentRoomId is null) return;
        string roomId = currentRoomId ?? "";
        if (AskReportReason(target.Length > 0 ? target : "this room") is not string reason) return;

        // The report, of the user or the room
        _ = RunSafelyAsync("Reporting", async client =>
        {
            if (target.Length > 0) await client.ReportUserAsync(target, reason);
            else await client.ReportRoomAsync(roomId, reason);
            app.Invoke(() => ShowStatus($"Reported {(target.Length > 0 ? target : "this room")} to the admins of your server."));
        });
    }

    /// <summary>Block, i.e. ignore, <paramref name="argument"/>, or stop; with no argument, list the blocked users.</summary>
    private void SetIgnored(string argument, bool ignored)
    {
        // A list without a user ID
        string userId = argument.Trim();
        if (userId.Length == 0)
        {
            IReadOnlyList<string> blocked = processor.Read(r => r?.IgnoredUsers.Order(StringComparer.Ordinal).ToList() ?? []);
            ShowStatus(blocked.Count == 0 ? "No one is blocked." : $"Blocked: {string.Join(", ", blocked)}");
            return;
        }
        if (!UserId.TryParse(userId, out _) || userId == ownUserId)
        {
            ShowStatus($"\"{userId}\" is not another user's ID; it looks like @alice:matrix.org.");
            return;
        }

        // Change on the server, then show the room again without, or with, their messages
        _ = RunSafelyAsync(ignored ? "Blocking" : "Unblocking", async client =>
        {
            await processor.SetIgnoredAsync(client, userId, ignored);
            app.Invoke(() =>
            {
                ShowStatus(ignored ? $"Blocked {userId}: their messages and invites no longer show. /unignore {userId} undoes it." : $"Unblocked {userId}.");
                if (currentRoomId is string roomId) _ = ReloadTimelineAsync(roomId);
            });
        });
    }

    /// <summary>Delete, i.e. deactivate, the account after a confirmation and the password, then quit and forget the user ID.</summary>
    private void DeleteAccount()
    {
        // Confirm, then the password
        if (!Dialogs.Confirm(app, "Delete account", "Delete your account for good? You leave every room, your name and picture are removed and every device is signed out. The user ID cannot be used again.")) return;
        if (Dialogs.Ask(app, "Delete account", "Your password, to confirm:", secret: true) is not string password) return;

        // Deactivate, forget the token and the user ID, and quit
        _ = RunSafelyAsync("Deleting the account", async client =>
        {
            try
            {
                await client.DeactivateAccountAsync(password);
            }
            catch (UserInteractiveAuthException)
            {
                app.Invoke(() => Dialogs.Error(app, "Delete account", "The password was not accepted."));
                return;
            }
            await store.ClearAccessTokenAsync();
            app.Invoke(() =>
            {
                settings.Save(new Dictionary<string, string?> { [$"{Uniform.Matrix.Client.Configuration.MatrixOptions.Section}:{nameof(Uniform.Matrix.Client.Configuration.MatrixOptions.UserId)}"] = null });
                Dialogs.Info(app, "Delete account", "Your account is deleted.");
                app.RequestStop();
            });
        });
    }
}
