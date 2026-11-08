namespace Uniform.Chat.Terminal.Ui;

using System.Text;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

// Server administration for server admins: accounts, registration tokens, reports, rooms and server notices.
public sealed partial class MainWindow
{
    /// <summary>Ask for a user ID when <paramref name="argument"/> is empty; return a valid one, or null after telling why not.</summary>
    private string? AskUserId(string title, string argument)
    {
        // Given or asked, then checked
        string? text = argument.Trim().Length > 0 ? argument.Trim() : Dialogs.Ask(app, title, $"User, e.g. @alice:{OwnServerName}:");
        if (text is null) return null;
        string userId = text.StartsWith('@') ? text : $"@{text}:{OwnServerName}";
        if (UserId.TryParse(userId, out _)) return userId;
        ShowStatus($"\"{text}\" is not a user ID; it looks like @alice:{OwnServerName}.");
        // On an invalid user ID
        return null;
    }

    /// <summary>Show the devices of a user, with when and where each was last used.</summary>
    private void ShowWhois(string argument)
    {
        // User, then the devices
        if (AskUserId("Who is", argument) is not string userId) return;
        _ = RunSafelyAsync("Looking up the user", async client =>
        {
            WhoisResponse whois = await client.WhoisAsync(userId);
            StringBuilder text = new();
            foreach ((string deviceId, WhoisDevice device) in whois.Devices.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                WhoisConnection? last = device.Sessions.SelectMany(s => s.Connections).OrderByDescending(c => c.LastSeen).FirstOrDefault();
                string seen = last?.LastSeen is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";
                text.AppendLine($"{deviceId}  {device.DisplayName ?? ""}\n    last seen {seen} from {last?.Ip ?? "unknown"}");
            }
            app.Invoke(() => Dialogs.ShowText(app, $"Who is {userId}", text.Length == 0 ? "No devices are logged in." : text.ToString()));
        });
    }

    /// <summary>List the server's accounts, matching <paramref name="argument"/> or asked text, and act on the chosen one.</summary>
    private void ManageAccounts(string argument)
    {
        // Filter, then the list
        string? filter = argument.Length > 0 ? argument : Dialogs.AskAllowEmpty(app, "Accounts", "Part of a user ID or name; empty for all:");
        if (filter is null) return;
        _ = RunSafelyAsync("Listing accounts", async client =>
        {
            AdminAccountsResponse page = await client.GetAdminAccountsAsync(filter.Length > 0 ? filter : null, includeDeactivated: true, limit: 500);
            app.Invoke(() =>
            {
                List<string> rows = ["+ New account…", .. page.Users.Select(Describe)];
                string more = page.NextToken is null ? "" : $", first {page.Users.Count} shown";
                if (Dialogs.PickIndex(app, "Accounts", $"{page.Total} accounts{more}:", rows) is not int index) return;
                if (index == 0) CreateAccount();
                else ActOnAccount(page.Users[index - 1]);
            });
        });
    }

    /// <summary>Return one line about an account: its ID, name, flags and use.</summary>
    private static string Describe(AdminAccount account)
    {
        // Flags in brackets
        IEnumerable<string> flags = new[] { account.Admin ? "admin" : null, account.Suspended ? "suspended" : null, account.Locked ? "locked" : null, account.Deactivated ? "deactivated" : null }.OfType<string>();
        string tags = string.Concat(flags.Select(f => $" [{f}]"));
        // Return line
        return $"{account.Name}  {account.DisplayName}{tags}  {account.Devices ?? 0} devices, {account.JoinedRooms ?? 0} rooms";
    }

    /// <summary>Ask what to do with <paramref name="account"/>, and do it.</summary>
    private void ActOnAccount(AdminAccount account)
    {
        // Actions that fit its state
        string userId = account.Name;
        List<(string Title, Action Run)> actions =
        [
            ("Who is: devices and addresses", () => ShowWhois(userId)),
            ("Send a server notice…", () => SendServerNotice(userId)),
            ("Reset password…", () => ResetPassword(userId)),
            (account.Suspended ? "End suspension" : "Suspend: may read, not send", () => SetSuspended(userId, !account.Suspended)),
            (account.Locked ? "Unlock" : "Lock: refuse every session", () => SetLocked(userId, !account.Locked)),
            (account.Admin ? "Take server admin rights" : "Make server admin", () => SetServerAdmin(userId, !account.Admin)),
        ];
        if (!account.Deactivated) actions.Add(("Deactivate for good…", () => DeactivateUser(userId)));

        // Chosen action
        if (Dialogs.PickIndex(app, userId, Describe(account), [.. actions.Select(a => a.Title)]) is not int index) return;
        actions[index].Run();
    }

    /// <summary>Create an account with a password, asking for its name and password.</summary>
    private void CreateAccount()
    {
        // Name and password
        if (AskUserId("New account", "") is not string userId) return;
        if (Dialogs.Ask(app, "New account", $"Password for {userId}:", secret: true) is not string password || password.Length == 0) return;
        _ = RunSafelyAsync("Creating the account", async client =>
        {
            await client.CreateAdminAccountAsync(userId, password);
            app.Invoke(() => ShowStatus($"Created {userId}."));
        });
    }

    /// <summary>Set a new password for <paramref name="userId"/>, logging out their devices.</summary>
    private void ResetPassword(string userId)
    {
        // New password, then the change
        if (Dialogs.Ask(app, "Reset password", $"New password for {userId}; their devices are logged out:", secret: true) is not string password || password.Length == 0) return;
        _ = RunSafelyAsync("Resetting the password", async client =>
        {
            await client.ResetPasswordAsync(userId, password);
            app.Invoke(() => ShowStatus($"Set a new password for {userId} and logged out their devices."));
        });
    }

    /// <summary>Suspend <paramref name="argument"/>'s account, or end the suspension.</summary>
    private void SetSuspended(string argument, bool suspended)
    {
        // User, then the flag
        if (AskUserId(suspended ? "Suspend" : "End suspension", argument) is not string userId) return;
        _ = RunSafelyAsync(suspended ? "Suspending" : "Ending the suspension", async client =>
        {
            await client.SetSuspendedAsync(userId, suspended);
            app.Invoke(() => ShowStatus(suspended ? $"Suspended {userId}: they can read and leave rooms, but not send, join or invite. /unsuspend {userId} ends it." : $"Ended the suspension of {userId}."));
        });
    }

    /// <summary>Lock <paramref name="argument"/>'s account, or unlock it.</summary>
    private void SetLocked(string argument, bool locked)
    {
        // User, then the flag
        if (AskUserId(locked ? "Lock" : "Unlock", argument) is not string userId) return;
        _ = RunSafelyAsync(locked ? "Locking" : "Unlocking", async client =>
        {
            await client.SetLockedAsync(userId, locked);
            app.Invoke(() => ShowStatus(locked ? $"Locked {userId}: the server refuses their sessions until /unlock {userId}." : $"Unlocked {userId}."));
        });
    }

    /// <summary>Give or take server admin rights.</summary>
    private void SetServerAdmin(string userId, bool admin)
    {
        // Confirm giving rights, which are wide
        if (admin && !Dialogs.Confirm(app, "Make server admin", $"Let {userId} manage every account and room of the server?")) return;
        _ = RunSafelyAsync(admin ? "Making admin" : "Taking admin rights", async client =>
        {
            await client.SetServerAdminAsync(userId, admin);
            app.Invoke(() => ShowStatus(admin ? $"{userId} is a server admin." : $"{userId} is no longer a server admin."));
        });
    }

    /// <summary>Deactivate an account after a confirmation.</summary>
    private void DeactivateUser(string userId)
    {
        // Confirm, as it cannot be undone
        if (!Dialogs.Confirm(app, "Deactivate", $"Deactivate {userId} for good? They leave every room and cannot log in again; the user ID is not given out again.")) return;
        _ = RunSafelyAsync("Deactivating", async client =>
        {
            await client.DeactivateUserAsync(userId);
            app.Invoke(() => ShowStatus($"Deactivated {userId}."));
        });
    }

    /// <summary>Send a server notice: <paramref name="argument"/> is the user and the text, either asked when missing.</summary>
    private void SendServerNotice(string argument)
    {
        // User and text
        string[] parts = argument.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (AskUserId("Server notice", parts.Length > 0 ? parts[0] : "") is not string userId) return;
        string? text = parts.Length > 1 ? parts[1] : Dialogs.Ask(app, "Server notice", $"Notice to {userId}; it arrives in their Server notices room:");
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = RunSafelyAsync("Sending the notice", async client =>
        {
            await client.SendServerNoticeAsync(userId, text);
            app.Invoke(() => ShowStatus($"Sent a server notice to {userId}."));
        });
    }

    /// <summary>List the registration tokens; pick one to delete it, or create a new one.</summary>
    private void ManageRegistrationTokens()
    {
        // Tokens with their uses and expiry
        _ = RunSafelyAsync("Listing registration tokens", async client =>
        {
            IReadOnlyList<AdminRegistrationToken> tokens = await client.GetRegistrationTokensAsync();
            app.Invoke(() =>
            {
                List<string> rows = ["+ New token…", .. tokens.Select(Describe)];
                if (Dialogs.PickIndex(app, "Registration tokens", "New accounts need one of these when registration asks for a token:", rows) is not int index) return;
                if (index == 0) CreateRegistrationToken();
                else DeleteRegistrationToken(tokens[index - 1].Token);
            });
        });
    }

    /// <summary>Return one line about a registration token: the token, its uses and its expiry.</summary>
    private static string Describe(AdminRegistrationToken token)
    {
        // Uses and expiry
        string uses = token.UsesAllowed is int allowed ? $"used {token.Completed} of {allowed}" : $"used {token.Completed}, no limit";
        string expiry = token.ExpiryTime is long ms ? $"until {DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime():yyyy-MM-dd HH:mm}" : "no expiry";
        // Return line
        return $"{token.Token}  {uses}, {expiry}";
    }

    /// <summary>Create a registration token, asking for its text, uses and days valid.</summary>
    private void CreateRegistrationToken()
    {
        // Text, uses and days; empty means random, unlimited and forever
        if (Dialogs.AskAllowEmpty(app, "New token", "Token; empty for a random one:") is not string token) return;
        if (Dialogs.AskAllowEmpty(app, "New token", "How many accounts may use it; empty for any number:") is not string usesText) return;
        if (Dialogs.AskAllowEmpty(app, "New token", "Days it is valid; empty for no expiry:") is not string daysText) return;
        int? uses = int.TryParse(usesText, out int u) && u > 0 ? u : null;
        DateTimeOffset? expiry = double.TryParse(daysText, out double days) && days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null;

        // Create, and show it for copying
        _ = RunSafelyAsync("Creating the token", async client =>
        {
            AdminRegistrationToken created = await client.CreateRegistrationTokenAsync(token.Trim().Length > 0 ? token.Trim() : null, uses, expiry);
            app.Invoke(() => Dialogs.ShowCopyable(app, "New token", Describe(created), created.Token));
        });
    }

    /// <summary>Delete a registration token after a confirmation.</summary>
    private void DeleteRegistrationToken(string token)
    {
        // Confirm, then delete
        if (!Dialogs.Confirm(app, "Delete token", $"Delete the token {token}? Accounts made with it stay.")) return;
        _ = RunSafelyAsync("Deleting the token", async client =>
        {
            await client.DeleteRegistrationTokenAsync(token);
            app.Invoke(() => ShowStatus($"Deleted the token {token}."));
        });
    }

    /// <summary>List users' reports, newest first, and act on the chosen one.</summary>
    private void ManageReports()
    {
        // Reports with their target and reason
        _ = RunSafelyAsync("Listing reports", async client =>
        {
            AdminReportsResponse page = await client.GetReportsAsync(limit: 500);
            app.Invoke(() =>
            {
                if (page.EventReports.Count == 0)
                {
                    Dialogs.Info(app, "Reports", "No reports.");
                    return;
                }
                if (Dialogs.PickIndex(app, "Reports", $"{page.Total} reports, newest first:", [.. page.EventReports.Select(Describe)]) is not int index) return;
                ActOnReport(page.EventReports[index]);
            });
        });
    }

    /// <summary>Return one line about a report: when, who, what and why.</summary>
    private static string Describe(AdminReport report)
    {
        // Target: a message, a user or a room
        string target = report.EventId is not null ? $"message in {report.RoomId}" : report.TargetUserId ?? report.RoomId ?? "?";
        string when = DateTimeOffset.FromUnixTimeMilliseconds(report.ReceivedTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        // Return line
        return $"{when}{(report.Resolved ? " [resolved]" : "")}  {report.UserId} → {target}: {report.Reason}";
    }

    /// <summary>Ask what to do about <paramref name="report"/>: suspend the reported user, shut the room down or delete the report.</summary>
    private void ActOnReport(AdminReport report)
    {
        // The reported user: the message's sender, or the user reported
        string? sender = report.EventSummary?.Split(": ", 2)[0] is string first && UserId.TryParse(first, out _) ? first : null;
        string? reported = report.TargetUserId ?? sender;
        List<(string Title, Action Run)> actions = [("Delete the report", () => DeleteReport(report.Id))];
        if (reported is not null) actions.Add(($"Suspend {reported}", () => SetSuspended(reported, true)));
        if (report.RoomId is string roomId) actions.Add(("Shut the room down…", () => ShutdownRoom(roomId)));

        // Details above the choices
        string details = report.EventSummary is string summary ? $"{Describe(report)}\n{summary}" : Describe(report);
        if (Dialogs.PickIndex(app, $"Report {report.Id}", details, [.. actions.Select(a => a.Title)]) is not int index) return;
        actions[index].Run();
    }

    /// <summary>Delete a report.</summary>
    private void DeleteReport(long reportId) => _ = RunSafelyAsync("Deleting the report", async client =>
    {
        // Delete, then tell
        await client.DeleteReportAsync(reportId);
        app.Invoke(() => ShowStatus($"Deleted report {reportId}."));
    });

    /// <summary>List the server's rooms, matching <paramref name="argument"/> or asked text, and act on the chosen one.</summary>
    private void ManageServerRooms(string argument)
    {
        // Filter, then the list
        string? filter = argument.Length > 0 ? argument : Dialogs.AskAllowEmpty(app, "Rooms", "Part of a room name, alias or ID; empty for all:");
        if (filter is null) return;
        _ = RunSafelyAsync("Listing rooms", async client =>
        {
            AdminRoomsResponse page = await client.GetAdminRoomsAsync(filter.Length > 0 ? filter : null, limit: 500);
            app.Invoke(() =>
            {
                string more = page.NextBatch is null ? "" : $", first {page.Rooms.Count} shown";
                if (Dialogs.PickIndex(app, "Rooms", $"{page.TotalRooms} rooms, most members first{more}:", [.. page.Rooms.Select(Describe)]) is not int index) return;
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
        return $"{title}{id}  {room.JoinedMembers} members{(room.Public ? " [public]" : "")}{(room.Encryption is not null ? " [encrypted]" : "")}";
    }

    /// <summary>Ask what to do with a room of the server: list its members or shut it down.</summary>
    private void ActOnServerRoom(AdminRoomInfo room)
    {
        // Members or shutdown
        int? choice = Dialogs.PickIndex(app, room.Name ?? room.RoomId, Describe(room), ["Members", "Shut down…"]);
        if (choice == 0) _ = RunSafelyAsync("Listing members", async client =>
        {
            IReadOnlyList<string> members = await client.GetAdminRoomMembersAsync(room.RoomId);
            app.Invoke(() => Dialogs.ShowText(app, $"Members of {room.Name ?? room.RoomId}", members.Count == 0 ? "No members." : string.Join('\n', members)));
        });
        if (choice == 1) ShutdownRoom(room.RoomId);
    }

    /// <summary>Shut a room down on this server after asking whether to block it and for a reason.</summary>
    private void ShutdownRoom(string roomId)
    {
        // Block or not, then the reason members see
        int? choice = Dialogs.Choose(app, "Shut down", $"Make every member of this server leave {roomId}, and delete its addresses here? Blocking also keeps them from joining again.", ["_Cancel", "Shut _down", "Shut down and _block"]);
        if (choice is not (1 or 2)) return;
        string? reason = Dialogs.AskAllowEmpty(app, "Shut down", "Reason shown to the members; empty for none:");
        if (reason is null) return;

        // Shut down, then tell what was done
        _ = RunSafelyAsync("Shutting the room down", async client =>
        {
            AdminRoomShutdown done = await client.ShutdownRoomAsync(roomId, block: choice == 2, reason.Length > 0 ? reason : null);
            string failed = done.FailedToKickUsers.Count > 0 ? $"; {done.FailedToKickUsers.Count} could not be removed" : "";
            app.Invoke(() => ShowStatus($"Shut {roomId} down: {done.KickedUsers.Count} members removed{failed}{(choice == 2 ? ", blocked" : "")}."));
        });
    }
}
