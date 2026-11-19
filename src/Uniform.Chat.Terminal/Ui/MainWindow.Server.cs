namespace Uniform.Chat.Terminal.Ui;

using System.Text;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Chat.Terminal.Localization;

// Server administration for server admins: accounts, registration tokens, reports, rooms and server notices.
public sealed partial class MainWindow
{
    /// <summary>Ask for a user ID when <paramref name="argument"/> is empty; return a valid one, or null after telling why not.</summary>
    private string? AskUserId(string title, string argument)
    {
        // Given or asked, then checked
        string? text = argument.Trim().Length > 0 ? argument.Trim() : Dialogs.Ask(app, title, Texts.Format(Strings.UserPromptOnServer, OwnServerName));
        if (text is null) return null;
        string userId = text.StartsWith('@') ? text : $"@{text}:{OwnServerName}";
        if (UserId.TryParse(userId, out _)) return userId;
        ShowStatus(Texts.Format(Strings.NotAUserIdOnServer, text, OwnServerName));
        // On an invalid user ID
        return null;
    }

    /// <summary>Show the devices of a user, with when and where each was last used.</summary>
    private void ShowWhois(string argument)
    {
        // User, then the devices
        if (AskUserId(Strings.WhoIs, argument) is not string userId) return;
        _ = RunSafelyAsync(Strings.LookingUpUser, async client =>
        {
            WhoisResponse whois = await client.WhoisAsync(userId);
            StringBuilder text = new();
            foreach ((string deviceId, WhoisDevice device) in whois.Devices.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                WhoisConnection? last = device.Sessions.SelectMany(s => s.Connections).OrderByDescending(c => c.LastSeen).FirstOrDefault();
                string seen = last?.LastSeen is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Strings.Never;
                text.AppendLine(Texts.Format(Strings.DeviceLastSeen, deviceId, device.DisplayName ?? "", seen, last?.Ip ?? Strings.Unknown));
            }
            app.Invoke(() => Dialogs.ShowText(app, Texts.Format(Strings.WhoIsUser, userId), text.Length == 0 ? Strings.NoDevicesLoggedIn : text.ToString()));
        });
    }

    /// <summary>List the server's accounts, matching <paramref name="argument"/> or asked text, and act on the chosen one.</summary>
    private void ManageAccounts(string argument)
    {
        // Filter, then the list
        string? filter = argument.Length > 0 ? argument : Dialogs.AskAllowEmpty(app, Strings.Accounts, Strings.AccountFilterPrompt);
        if (filter is null) return;
        _ = RunSafelyAsync(Strings.ListingAccounts, async client =>
        {
            AdminAccountsResponse page = await client.GetAdminAccountsAsync(filter.Length > 0 ? filter : null, includeDeactivated: true, limit: 500);
            app.Invoke(() =>
            {
                List<string> rows = [Strings.NewAccountRow, .. page.Users.Select(Describe)];
                string more = page.NextToken is null ? "" : Texts.Format(Strings.FirstShown, page.Users.Count);
                if (Dialogs.PickIndex(app, Strings.Accounts, Texts.Format(Strings.AccountsCount, page.Total, more), rows) is not int index) return;
                if (index == 0) CreateAccount();
                else ActOnAccount(page.Users[index - 1]);
            });
        });
    }

    /// <summary>Return one line about an account: its ID, name, flags and use.</summary>
    private static string Describe(AdminAccount account)
    {
        // Flags in brackets
        IEnumerable<string> flags = new[] { account.Admin ? Strings.FlagAdmin : null, account.Suspended ? Strings.FlagSuspended : null, account.Locked ? Strings.FlagLocked : null, account.Deactivated ? Strings.FlagDeactivated : null }.OfType<string>();
        string tags = string.Concat(flags.Select(f => $" [{f}]"));
        // Return line
        return Texts.Format(Strings.AccountRow, account.Name, account.DisplayName, tags, account.Devices ?? 0, account.JoinedRooms ?? 0);
    }

    /// <summary>Ask what to do with <paramref name="account"/>, and do it.</summary>
    private void ActOnAccount(AdminAccount account)
    {
        // Actions that fit its state
        string userId = account.Name;
        List<(string Title, Action Run)> actions =
        [
            (Strings.WhoIsAction, () => ShowWhois(userId)),
            (Strings.SendServerNoticeAction, () => SendServerNotice(userId)),
            (Strings.ResetPasswordAction, () => ResetPassword(userId)),
            (account.Suspended ? Strings.EndSuspension : Strings.SuspendAction, () => SetSuspended(userId, !account.Suspended)),
            (account.Locked ? Strings.Unlock : Strings.LockAction, () => SetLocked(userId, !account.Locked)),
            (account.Admin ? Strings.TakeServerAdmin : Strings.MakeServerAdmin, () => SetServerAdmin(userId, !account.Admin)),
        ];
        if (!account.Deactivated) actions.Add((Strings.DeactivateAction, () => DeactivateUser(userId)));

        // Chosen action
        if (Dialogs.PickIndex(app, userId, Describe(account), [.. actions.Select(a => a.Title)]) is not int index) return;
        actions[index].Run();
    }

    /// <summary>Create an account with a password, asking for its name and password.</summary>
    private void CreateAccount()
    {
        // Name and password
        if (AskUserId(Strings.NewAccount, "") is not string userId) return;
        if (Dialogs.Ask(app, Strings.NewAccount, Texts.Format(Strings.PasswordFor, userId), secret: true) is not string password || password.Length == 0) return;
        _ = RunSafelyAsync(Strings.CreatingAccount, async client =>
        {
            await client.CreateAdminAccountAsync(userId, password);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.CreatedUser, userId)));
        });
    }

    /// <summary>Set a new password for <paramref name="userId"/>, logging out their devices.</summary>
    private void ResetPassword(string userId)
    {
        // New password, then the change
        if (Dialogs.Ask(app, Strings.ResetPassword, Texts.Format(Strings.NewPasswordFor, userId), secret: true) is not string password || password.Length == 0) return;
        _ = RunSafelyAsync(Strings.ResettingPassword, async client =>
        {
            await client.ResetPasswordAsync(userId, password);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.PasswordReset, userId)));
        });
    }

    /// <summary>Suspend <paramref name="argument"/>'s account, or end the suspension.</summary>
    private void SetSuspended(string argument, bool suspended)
    {
        // User, then the flag
        if (AskUserId(suspended ? Strings.Suspend : Strings.EndSuspension, argument) is not string userId) return;
        _ = RunSafelyAsync(suspended ? Strings.Suspending : Strings.EndingSuspension, async client =>
        {
            await client.SetSuspendedAsync(userId, suspended);
            app.Invoke(() => ShowStatus(suspended ? Texts.Format(Strings.SuspendedUser, userId, userId) : Texts.Format(Strings.EndedSuspension, userId)));
        });
    }

    /// <summary>Lock <paramref name="argument"/>'s account, or unlock it.</summary>
    private void SetLocked(string argument, bool locked)
    {
        // User, then the flag
        if (AskUserId(locked ? Strings.Lock : Strings.Unlock, argument) is not string userId) return;
        _ = RunSafelyAsync(locked ? Strings.Locking : Strings.Unlocking, async client =>
        {
            await client.SetLockedAsync(userId, locked);
            app.Invoke(() => ShowStatus(locked ? Texts.Format(Strings.LockedUser, userId, userId) : Texts.Format(Strings.UnlockedUser, userId)));
        });
    }

    /// <summary>Give or take server admin rights.</summary>
    private void SetServerAdmin(string userId, bool admin)
    {
        // Confirm giving rights, which are wide
        if (admin && !Dialogs.Confirm(app, Strings.MakeServerAdmin, Texts.Format(Strings.MakeServerAdminQuestion, userId))) return;
        _ = RunSafelyAsync(admin ? Strings.MakingAdmin : Strings.TakingAdminRights, async client =>
        {
            await client.SetServerAdminAsync(userId, admin);
            app.Invoke(() => ShowStatus(admin ? Texts.Format(Strings.IsServerAdmin, userId) : Texts.Format(Strings.NoLongerServerAdmin, userId)));
        });
    }

    /// <summary>Deactivate an account after a confirmation.</summary>
    private void DeactivateUser(string userId)
    {
        // Confirm, as it cannot be undone
        if (!Dialogs.Confirm(app, Strings.Deactivate, Texts.Format(Strings.DeactivateQuestion, userId))) return;
        _ = RunSafelyAsync(Strings.Deactivating, async client =>
        {
            await client.DeactivateUserAsync(userId);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.DeactivatedUser, userId)));
        });
    }

    /// <summary>Send a server notice: <paramref name="argument"/> is the user and the text, either asked when missing.</summary>
    private void SendServerNotice(string argument)
    {
        // User and text
        string[] parts = argument.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (AskUserId(Strings.ServerNotice, parts.Length > 0 ? parts[0] : "") is not string userId) return;
        string? text = parts.Length > 1 ? parts[1] : Dialogs.Ask(app, Strings.ServerNotice, Texts.Format(Strings.NoticePrompt, userId));
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = RunSafelyAsync(Strings.SendingNotice, async client =>
        {
            await client.SendServerNoticeAsync(userId, text);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.SentNotice, userId)));
        });
    }

    /// <summary>List the registration tokens; pick one to delete it, or create a new one.</summary>
    private void ManageRegistrationTokens()
    {
        // Tokens with their uses and expiry
        _ = RunSafelyAsync(Strings.ListingTokens, async client =>
        {
            IReadOnlyList<AdminRegistrationToken> tokens = await client.GetRegistrationTokensAsync();
            app.Invoke(() =>
            {
                List<string> rows = [Strings.NewTokenRow, .. tokens.Select(Describe)];
                if (Dialogs.PickIndex(app, Strings.RegistrationTokens, Strings.TokensPrompt, rows) is not int index) return;
                if (index == 0) CreateRegistrationToken();
                else DeleteRegistrationToken(tokens[index - 1].Token);
            });
        });
    }

    /// <summary>Return one line about a registration token: the token, its uses and its expiry.</summary>
    private static string Describe(AdminRegistrationToken token)
    {
        // Uses and expiry
        string uses = token.UsesAllowed is int allowed ? Texts.Format(Strings.TokenUsesOf, token.Completed, allowed) : Texts.Format(Strings.TokenUsesNoLimit, token.Completed);
        string expiry = token.ExpiryTime is long ms ? Texts.Format(Strings.TokenUntil, DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()) : Strings.NoExpiry;
        // Return line
        return $"{token.Token}  {uses}, {expiry}";
    }

    /// <summary>Create a registration token, asking for its text, uses and days valid.</summary>
    private void CreateRegistrationToken()
    {
        // Text, uses and days; empty means random, unlimited and forever
        if (Dialogs.AskAllowEmpty(app, Strings.NewToken, Strings.TokenPrompt) is not string token) return;
        if (Dialogs.AskAllowEmpty(app, Strings.NewToken, Strings.TokenUsesPrompt) is not string usesText) return;
        if (Dialogs.AskAllowEmpty(app, Strings.NewToken, Strings.TokenDaysPrompt) is not string daysText) return;
        int? uses = int.TryParse(usesText, out int u) && u > 0 ? u : null;
        DateTimeOffset? expiry = double.TryParse(daysText, out double days) && days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null;

        // Create, and show it for copying
        _ = RunSafelyAsync(Strings.CreatingToken, async client =>
        {
            AdminRegistrationToken created = await client.CreateRegistrationTokenAsync(token.Trim().Length > 0 ? token.Trim() : null, uses, expiry);
            app.Invoke(() => Dialogs.ShowCopyable(app, Strings.NewToken, Describe(created), created.Token));
        });
    }

    /// <summary>Delete a registration token after a confirmation.</summary>
    private void DeleteRegistrationToken(string token)
    {
        // Confirm, then delete
        if (!Dialogs.Confirm(app, Strings.DeleteToken, Texts.Format(Strings.DeleteTokenQuestion, token))) return;
        _ = RunSafelyAsync(Strings.DeletingToken, async client =>
        {
            await client.DeleteRegistrationTokenAsync(token);
            app.Invoke(() => ShowStatus(Texts.Format(Strings.DeletedToken, token)));
        });
    }

    /// <summary>List users' reports, newest first, and act on the chosen one.</summary>
    private void ManageReports()
    {
        // Reports with their target and reason
        _ = RunSafelyAsync(Strings.ListingReports, async client =>
        {
            AdminReportsResponse page = await client.GetReportsAsync(limit: 500);
            app.Invoke(() =>
            {
                if (page.EventReports.Count == 0)
                {
                    Dialogs.Info(app, Strings.Reports, Strings.NoReports);
                    return;
                }
                if (Dialogs.PickIndex(app, Strings.Reports, Texts.Format(Strings.ReportsCount, page.Total), [.. page.EventReports.Select(Describe)]) is not int index) return;
                ActOnReport(page.EventReports[index]);
            });
        });
    }

    /// <summary>Return one line about a report: when, who, what and why.</summary>
    private static string Describe(AdminReport report)
    {
        // Target: a message, a user or a room
        string target = report.EventId is not null ? Texts.Format(Strings.MessageIn, report.RoomId) : report.TargetUserId ?? report.RoomId ?? "?";
        string when = DateTimeOffset.FromUnixTimeMilliseconds(report.ReceivedTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        // Return line
        return $"{when}{(report.Resolved ? Strings.FlagResolved : "")}  {report.UserId} → {target}: {report.Reason}";
    }

    /// <summary>Ask what to do about <paramref name="report"/>: suspend the reported user, shut the room down or delete the report.</summary>
    private void ActOnReport(AdminReport report)
    {
        // The reported user: the message's sender, or the user reported
        string? sender = report.EventSummary?.Split(": ", 2)[0] is string first && UserId.TryParse(first, out _) ? first : null;
        string? reported = report.TargetUserId ?? sender;
        List<(string Title, Action Run)> actions = [(Strings.DeleteTheReport, () => DeleteReport(report.Id))];
        if (reported is not null) actions.Add((Texts.Format(Strings.SuspendUser, reported), () => SetSuspended(reported, true)));
        if (report.RoomId is string roomId) actions.Add((Strings.ShutRoomDownAction, () => ShutdownRoom(roomId)));

        // Details above the choices
        string details = report.EventSummary is string summary ? $"{Describe(report)}\n{summary}" : Describe(report);
        if (Dialogs.PickIndex(app, Texts.Format(Strings.ReportTitle, report.Id), details, [.. actions.Select(a => a.Title)]) is not int index) return;
        actions[index].Run();
    }

    /// <summary>Delete a report.</summary>
    private void DeleteReport(long reportId) => _ = RunSafelyAsync(Strings.DeletingReport, async client =>
    {
        // Delete, then tell
        await client.DeleteReportAsync(reportId);
        app.Invoke(() => ShowStatus(Texts.Format(Strings.DeletedReport, reportId)));
    });

    /// <summary>List the server's rooms, matching <paramref name="argument"/> or asked text, and act on the chosen one.</summary>
    private void ManageServerRooms(string argument)
    {
        // Filter, then the list
        string? filter = argument.Length > 0 ? argument : Dialogs.AskAllowEmpty(app, Strings.Rooms, Strings.RoomFilterPrompt);
        if (filter is null) return;
        _ = RunSafelyAsync(Strings.ListingRooms, async client =>
        {
            AdminRoomsResponse page = await client.GetAdminRoomsAsync(filter.Length > 0 ? filter : null, limit: 500);
            app.Invoke(() =>
            {
                string more = page.NextBatch is null ? "" : Texts.Format(Strings.FirstShown, page.Rooms.Count);
                if (Dialogs.PickIndex(app, Strings.Rooms, Texts.Format(Strings.RoomsCount, page.TotalRooms, more), [.. page.Rooms.Select(Describe)]) is not int index) return;
                ActOnServerRoom(page.Rooms[index]);
            });
        });
    }

    /// <summary>Return one line about a room: its name or alias, members and flags.</summary>
    private static string Describe(AdminRoomInfo room)
    {
        // Name, then the ID when it has one
        string title = room.Name ?? room.CanonicalAlias ?? room.RoomId;
        string id = title == room.RoomId ? "" : $"  {room.RoomId}";
        // Return line
        return Texts.Format(Strings.RoomRow, title, id, room.JoinedMembers, (room.Public ? Strings.FlagPublic : ""), (room.Encryption is not null ? Strings.FlagEncrypted : ""));
    }

    /// <summary>Ask what to do with a room of the server: list its members or shut it down.</summary>
    private void ActOnServerRoom(AdminRoomInfo room)
    {
        // Members or shutdown
        int? choice = Dialogs.PickIndex(app, room.Name ?? room.RoomId, Describe(room), [Strings.Members, Strings.ShutDownAction]);
        if (choice == 0) _ = RunSafelyAsync(Strings.ListingMembers, async client =>
        {
            IReadOnlyList<string> members = await client.GetAdminRoomMembersAsync(room.RoomId);
            app.Invoke(() => Dialogs.ShowText(app, Texts.Format(Strings.MembersOf, room.Name ?? room.RoomId), members.Count == 0 ? Strings.NoMembers : string.Join('\n', members)));
        });
        if (choice == 1) ShutdownRoom(room.RoomId);
    }

    /// <summary>Shut a room down on this server after asking whether to block it and for a reason.</summary>
    private void ShutdownRoom(string roomId)
    {
        // Block or not, then the reason members see
        int? choice = Dialogs.Choose(app, Strings.ShutDown, Texts.Format(Strings.ShutDownQuestion, roomId), [Strings.ButtonCancel, Strings.ButtonShutDown, Strings.ButtonShutDownBlock]);
        if (choice is not (1 or 2)) return;
        string? reason = Dialogs.AskAllowEmpty(app, Strings.ShutDown, Strings.ShutDownReasonPrompt);
        if (reason is null) return;

        // Shut down, then tell what was done
        _ = RunSafelyAsync(Strings.ShuttingDown, async client =>
        {
            AdminRoomShutdown done = await client.ShutdownRoomAsync(roomId, block: choice == 2, reason.Length > 0 ? reason : null);
            string failed = done.FailedToKickUsers.Count > 0 ? Texts.Format(Strings.CouldNotRemove, done.FailedToKickUsers.Count) : "";
            app.Invoke(() => ShowStatus(Texts.Format(Strings.ShutDownDone, roomId, done.KickedUsers.Count, failed, (choice == 2 ? Strings.Blocked : ""))));
        });
    }
}
