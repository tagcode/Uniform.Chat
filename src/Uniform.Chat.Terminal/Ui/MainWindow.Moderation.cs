namespace Uniform.Chat.Terminal.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Chat.Terminal.Localization;

// Reporting messages, users and rooms, blocking users, and deleting the account.
public sealed partial class MainWindow
{
    /// <summary>Reasons offered when reporting, with hotkeys.</summary>
    private static string[] ReportReasons => [Strings.ReasonSpam, Strings.ReasonHarassment, Strings.ReasonSexualViolent, Strings.ReasonIllegal, Strings.ReasonOther];

    /// <summary>Ask why <paramref name="what"/> is reported; return the reason without its hotkey, or null when cancelled.</summary>
    private string? AskReportReason(string what)
    {
        // A reason from the list
        int? choice = Dialogs.Choose(app, Strings.Report, Texts.Format(Strings.ReportWhy, what), [.. ReportReasons, Strings.ButtonCancel]);
        // Return reason
        return choice is int index && index < ReportReasons.Length ? ReportReasons[index].Replace("_", "") : null;
    }

    /// <summary>Report <paramref name="clientEvent"/> of <paramref name="roomId"/>, asking why.</summary>
    private void ReportMessage(string roomId, ClientEvent clientEvent)
    {
        // Reason, then the report
        if (clientEvent.EventId is not string eventId || AskReportReason(Strings.ThisMessage) is not string reason) return;
        _ = RunSafelyAsync(Strings.ReportingMessage, async client =>
        {
            await client.ReportEventAsync(roomId, eventId, reason);
            app.Invoke(() => ShowStatus(Strings.ReportedMessage));
        });
    }

    /// <summary>Report the user of <paramref name="argument"/>, or the open room when it is empty, asking why.</summary>
    private void Report(string argument)
    {
        // A user ID, else the open room
        string target = argument.Trim();
        if (target.Length > 0 && !UserId.TryParse(target, out _))
        {
            ShowStatus(Texts.Format(Strings.ReportNotAUser, target));
            return;
        }
        if (target.Length == 0 && currentRoomId is null) return;
        string roomId = currentRoomId ?? "";
        if (AskReportReason(target.Length > 0 ? target : Strings.ThisRoom) is not string reason) return;

        // The report, of the user or the room
        _ = RunSafelyAsync(Strings.Reporting, async client =>
        {
            if (target.Length > 0) await client.ReportUserAsync(target, reason);
            else await client.ReportRoomAsync(roomId, reason);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.Reported, (target.Length > 0 ? target : Strings.ThisRoom))));
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
            ShowStatus(blocked.Count == 0 ? Strings.NoOneBlocked : Texts.Format(Strings.BlockedList, string.Join(", ", blocked)));
            return;
        }
        if (!UserId.TryParse(userId, out _) || userId == ownUserId)
        {
            ShowStatus(Texts.Format(Strings.NotAnotherUser, userId));
            return;
        }

        // Change on the server, then show the room again without, or with, their messages
        _ = RunSafelyAsync(ignored ? Strings.Blocking : Strings.Unblocking, async client =>
        {
            await processor.SetIgnoredAsync(client, userId, ignored);
            app.Invoke(() =>
            {
                ShowStatus(ignored ? Texts.Format(Strings.BlockedUser, userId, userId) : Texts.Format(Strings.UnblockedUser, userId));
                if (currentRoomId is string roomId) _ = ReloadTimelineAsync(roomId);
            });
        });
    }

    /// <summary>Delete, i.e. deactivate, the account after a confirmation and the password, then quit and forget the user ID.</summary>
    private void DeleteAccount()
    {
        // Confirm, then the password
        if (!Dialogs.Confirm(app, Strings.DeleteAccount, Strings.DeleteAccountQuestion)) return;
        if (Dialogs.Ask(app, Strings.DeleteAccount, Strings.YourPasswordToConfirm, secret: true) is not string password) return;

        // Deactivate, forget the token and the user ID, and quit
        _ = RunSafelyAsync(Strings.DeletingAccount, async client =>
        {
            try
            {
                await client.DeactivateAccountAsync(password);
            }
            catch (UserInteractiveAuthException)
            {
                app.Invoke(() => Dialogs.Error(app, Strings.DeleteAccount, Strings.PasswordNotAccepted));
                return;
            }
            await store.ClearAccessTokenAsync();
            app.Invoke(() =>
            {
                settings.Save(new Dictionary<string, string?> { [$"{Uniform.Matrix.Client.Configuration.MatrixOptions.Section}:{nameof(Uniform.Matrix.Client.Configuration.MatrixOptions.UserId)}"] = null });
                Dialogs.Info(app, Strings.DeleteAccount, Strings.AccountDeleted);
                app.RequestStop();
            });
        });
    }
}
