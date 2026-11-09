namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>A section of the server administration panel.</summary>
public enum AdminSection
{
    /// <summary>Users' reports.</summary>
    Reports,
    /// <summary>The server's accounts.</summary>
    Accounts,
    /// <summary>Registration tokens.</summary>
    Tokens,
}

/// <summary>A report as the panel lists it.</summary>
/// <param name="Report">The report.</param>
/// <param name="Title">Who reported what, and when.</param>
/// <param name="Details">Why, and the reported message when the server can read it.</param>
/// <param name="ReportedUser">The reported user, or the reported message's sender; null when unknown.</param>
public sealed record AdminReportItem(AdminReport Report, string Title, string Details, string? ReportedUser)
{
    /// <summary>Whether there is a user to suspend.</summary>
    public bool HasReportedUser => ReportedUser is not null;
    /// <summary>Whether there is a room to shut down.</summary>
    public bool HasRoom => Report.RoomId is not null;
}

/// <summary>An account as the panel lists it.</summary>
/// <param name="Account">The account.</param>
/// <param name="Details">Name, flags, devices and rooms.</param>
public sealed record AdminAccountItem(AdminAccount Account, string Details)
{
    /// <summary>The user ID.</summary>
    public string Name => Account.Name;
}

/// <summary>A registration token as the panel lists it.</summary>
/// <param name="Token">The token.</param>
/// <param name="Details">Uses and expiry.</param>
public sealed record AdminTokenItem(string Token, string Details);

/// <summary>Server administration for server admins, over the server's admin API: users' reports, accounts with suspension, locks, passwords, admin rights, notices and deactivation, and registration tokens.</summary>
/// <remarks>Changes that cannot be undone, deactivation and shutting a room down, wait for <see cref="ConfirmCommand"/>.</remarks>
public sealed partial class AdminViewModel : ViewModelBase
{
    private readonly ClientConnection connection;
    private Func<Task>? pending;

    /// <summary>Create the panel over <paramref name="connection"/>.</summary>
    public AdminViewModel(ClientConnection connection)
    {
        // Reports first
        this.connection = connection;
    }

    /// <summary>The section shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsReports), nameof(ShowsAccounts), nameof(ShowsTokens))]
    public partial AdminSection Section { get; set; }

    /// <summary>Whether the reports are shown.</summary>
    public bool ShowsReports => Section == AdminSection.Reports;
    /// <summary>Whether the accounts are shown.</summary>
    public bool ShowsAccounts => Section == AdminSection.Accounts;
    /// <summary>Whether the tokens are shown.</summary>
    public bool ShowsTokens => Section == AdminSection.Tokens;

    /// <summary>Whether a request is running.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The result of the last action, or why it failed; empty for none.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>A change waiting for confirmation, as a question; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    public partial string Confirmation { get; set; } = "";

    /// <summary>Whether a change waits for confirmation.</summary>
    public bool IsConfirming => Confirmation.Length > 0;

    /// <summary>Users' reports, newest first.</summary>
    public ObservableCollection<AdminReportItem> Reports { get; } = [];

    /// <summary>Accounts matching <see cref="Search"/>.</summary>
    public ObservableCollection<AdminAccountItem> Accounts { get; } = [];

    /// <summary>Part of a user ID or name to find accounts by; empty for all.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>The account whose actions are shown, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount), nameof(SuspendText), nameof(LockText), nameof(AdminText))]
    public partial AdminAccountItem? SelectedAccount { get; set; }

    /// <summary>Whether an account is chosen.</summary>
    public bool HasSelectedAccount => SelectedAccount is not null;
    /// <summary>Text of the suspension button of the chosen account.</summary>
    public string SuspendText => SelectedAccount?.Account.Suspended == true ? "End suspension" : "Suspend";
    /// <summary>Text of the lock button of the chosen account.</summary>
    public string LockText => SelectedAccount?.Account.Locked == true ? "Unlock" : "Lock";
    /// <summary>Text of the admin button of the chosen account.</summary>
    public string AdminText => SelectedAccount?.Account.Admin == true ? "Take admin rights" : "Make admin";

    /// <summary>New password for the chosen account.</summary>
    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    /// <summary>Server notice to send to the chosen account.</summary>
    [ObservableProperty]
    public partial string NoticeText { get; set; } = "";

    /// <summary>The chosen account's devices, from whois.</summary>
    [ObservableProperty]
    public partial string Devices { get; set; } = "";

    /// <summary>Registration tokens.</summary>
    public ObservableCollection<AdminTokenItem> Tokens { get; } = [];

    /// <summary>How many accounts a new token may make; empty for any number.</summary>
    [ObservableProperty]
    public partial string NewTokenUses { get; set; } = "";

    /// <summary>Days a new token is valid; empty for no expiry.</summary>
    [ObservableProperty]
    public partial string NewTokenDays { get; set; } = "";

    /// <summary>The token just made, to copy and share; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreatedToken))]
    public partial string CreatedToken { get; set; } = "";

    /// <summary>Whether a token was just made.</summary>
    public bool HasCreatedToken => CreatedToken.Length > 0;

    /// <summary>Show a section and load it.</summary>
    [RelayCommand]
    private Task ShowSectionAsync(AdminSection section)
    {
        // Switch, then load
        Section = section;
        // Done
        return RefreshAsync();
    }

    /// <summary>Load the shown section again.</summary>
    [RelayCommand]
    public Task RefreshAsync() => Section switch
    {
        AdminSection.Reports => RunAsync("Loading reports", LoadReportsAsync),
        AdminSection.Accounts => RunAsync("Loading accounts", LoadAccountsAsync),
        _ => RunAsync("Loading tokens", LoadTokensAsync),
    };

    /// <summary>Delete a report.</summary>
    public Task DeleteReportAsync(AdminReportItem item) => RunAsync("Deleting the report", async client =>
    {
        // Delete, then list again
        await client.DeleteReportAsync(item.Report.Id);
        await LoadReportsAsync(client);
        Message = "Report deleted.";
    });

    /// <summary>Suspend the reported user.</summary>
    public Task SuspendReportedAsync(AdminReportItem item) => item.ReportedUser is not string userId ? Task.CompletedTask : RunAsync("Suspending", async client =>
    {
        // Suspend, then tell
        await client.SetSuspendedAsync(userId, true);
        Message = $"Suspended {userId}: they can read but not send, join or invite.";
    });

    /// <summary>Ask to shut down the reported room, which cannot be undone.</summary>
    public void ShutDownRoom(AdminReportItem item)
    {
        // Confirmed later
        if (item.Report.RoomId is not string roomId) return;
        Ask($"Shut down {roomId}? Every member from this server leaves it, its addresses here are deleted, and nobody from this server can join it again.", () => RunAsync("Shutting the room down", async client =>
        {
            AdminRoomShutdown done = await client.ShutdownRoomAsync(roomId, block: true, "The room was shut down by the server's admins");
            Message = $"Shut down {roomId}: {done.KickedUsers.Count} members removed.";
        }));
    }

    /// <summary>Find accounts by <see cref="Search"/>.</summary>
    [RelayCommand]
    private Task SearchAccountsAsync() => RunAsync("Loading accounts", LoadAccountsAsync);

    /// <summary>Show the actions of an account, and its devices.</summary>
    public Task SelectAccountAsync(AdminAccountItem item)
    {
        // Chosen, then whois
        SelectedAccount = item;
        Devices = "";
        NewPassword = "";
        NoticeText = "";
        // Done
        return RunAsync("Looking up devices", async client =>
        {
            WhoisResponse whois = await client.WhoisAsync(item.Name);
            Devices = whois.Devices.Count == 0 ? "No devices are signed in." : string.Join("\n", whois.Devices.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => DescribeDevice(d.Key, d.Value)));
        }, report: false);
    }

    /// <summary>Close the chosen account's actions.</summary>
    [RelayCommand]
    private void CloseAccount() => SelectedAccount = null;

    /// <summary>Suspend the chosen account, or end the suspension.</summary>
    [RelayCommand]
    private Task ToggleSuspendedAsync() => ChangeAccountAsync("Suspending", (client, account) => client.SetSuspendedAsync(account.Name, !account.Suspended), account => account.Suspended ? $"Ended the suspension of {account.Name}." : $"Suspended {account.Name}: they can read but not send, join or invite.");

    /// <summary>Lock the chosen account, or unlock it.</summary>
    [RelayCommand]
    private Task ToggleLockedAsync() => ChangeAccountAsync("Locking", (client, account) => client.SetLockedAsync(account.Name, !account.Locked), account => account.Locked ? $"Unlocked {account.Name}." : $"Locked {account.Name}: the server refuses their devices until unlocked.");

    /// <summary>Give or take the chosen account's admin rights.</summary>
    [RelayCommand]
    private Task ToggleAdminAsync() => ChangeAccountAsync("Changing admin rights", (client, account) => client.SetServerAdminAsync(account.Name, !account.Admin), account => account.Admin ? $"{account.Name} is no longer a server admin." : $"{account.Name} is a server admin.");

    /// <summary>Set <see cref="NewPassword"/> for the chosen account, logging out its devices.</summary>
    [RelayCommand]
    private Task ResetPasswordAsync() => NewPassword.Length == 0 ? Task.CompletedTask : ChangeAccountAsync("Resetting the password", (client, account) => client.ResetPasswordAsync(account.Name, NewPassword), account =>
    {
        // Forget the password, then tell
        NewPassword = "";
        return $"Set a new password for {account.Name} and logged out their devices.";
    });

    /// <summary>Send <see cref="NoticeText"/> to the chosen account as a server notice.</summary>
    [RelayCommand]
    private Task SendNoticeAsync() => NoticeText.Trim().Length == 0 ? Task.CompletedTask : ChangeAccountAsync("Sending the notice", (client, account) => client.SendServerNoticeAsync(account.Name, NoticeText.Trim()), account =>
    {
        // Clear, then tell
        NoticeText = "";
        return $"Sent a server notice to {account.Name}.";
    });

    /// <summary>Ask to deactivate the chosen account, which cannot be undone.</summary>
    [RelayCommand]
    private void Deactivate()
    {
        // Confirmed later
        if (SelectedAccount?.Account is not AdminAccount account) return;
        Ask($"Deactivate {account.Name} for good? They leave every room and cannot sign in again; the user ID is not given out again.", () => ChangeAccountAsync("Deactivating", (client, _) => client.DeactivateUserAsync(account.Name), _ => $"Deactivated {account.Name}."));
    }

    /// <summary>Make a registration token with <see cref="NewTokenUses"/> and <see cref="NewTokenDays"/>.</summary>
    [RelayCommand]
    private Task CreateTokenAsync() => RunAsync("Creating the token", async client =>
    {
        // Uses and days, empty for no limit
        int? uses = int.TryParse(NewTokenUses, NumberStyles.Integer, CultureInfo.CurrentCulture, out int u) && u > 0 ? u : null;
        DateTimeOffset? expiry = double.TryParse(NewTokenDays, NumberStyles.Float, CultureInfo.CurrentCulture, out double days) && days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null;
        AdminRegistrationToken token = await client.CreateRegistrationTokenAsync(null, uses, expiry);
        CreatedToken = token.Token;
        NewTokenUses = "";
        NewTokenDays = "";
        await LoadTokensAsync(client);
    });

    /// <summary>Delete a registration token.</summary>
    public Task DeleteTokenAsync(AdminTokenItem item) => RunAsync("Deleting the token", async client =>
    {
        // Delete, then list again
        await client.DeleteRegistrationTokenAsync(item.Token);
        if (CreatedToken == item.Token) CreatedToken = "";
        await LoadTokensAsync(client);
        Message = "Token deleted; accounts made with it stay.";
    });

    /// <summary>Do the change waiting for confirmation.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        // Once
        Func<Task>? action = pending;
        CancelConfirmation();
        if (action is not null) await action();
    }

    /// <summary>Drop the change waiting for confirmation.</summary>
    [RelayCommand]
    private void CancelConfirmation()
    {
        // Forget it
        pending = null;
        Confirmation = "";
    }

    /// <summary>Wait for confirmation of <paramref name="question"/> before <paramref name="action"/>.</summary>
    private void Ask(string question, Func<Task> action)
    {
        // Shown until confirmed or cancelled
        pending = action;
        Confirmation = question;
    }

    /// <summary>Change the chosen account, then load it again and tell <paramref name="result"/> of the account as it was.</summary>
    private Task ChangeAccountAsync(string what, Func<MatrixClient, AdminAccount, Task> change, Func<AdminAccount, string> result) => SelectedAccount?.Account is not AdminAccount account ? Task.CompletedTask : RunAsync(what, async client =>
    {
        // Change, reload the list and the chosen one
        await change(client, account);
        string text = result(account);
        await LoadAccountsAsync(client);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Name == account.Name);
        Message = text;
    });

    /// <summary>Load the reports.</summary>
    private async Task LoadReportsAsync(MatrixClient client)
    {
        // Newest first, as the server lists them
        AdminReportsResponse page = await client.GetReportsAsync(limit: 500);
        Reports.Clear();
        foreach (AdminReport report in page.EventReports) Reports.Add(Describe(report));
        // Done
        if (Reports.Count == 0) Message = "No reports.";
    }

    /// <summary>Load the accounts matching <see cref="Search"/>.</summary>
    private async Task LoadAccountsAsync(MatrixClient client)
    {
        // Deactivated ones too
        AdminAccountsResponse page = await client.GetAdminAccountsAsync(Search.Trim().Length > 0 ? Search.Trim() : null, includeDeactivated: true, limit: 500);
        Accounts.Clear();
        foreach (AdminAccount account in page.Users) Accounts.Add(new AdminAccountItem(account, Describe(account)));
    }

    /// <summary>Load the registration tokens.</summary>
    private async Task LoadTokensAsync(MatrixClient client)
    {
        // Each with its uses and expiry
        IReadOnlyList<AdminRegistrationToken> tokens = await client.GetRegistrationTokensAsync();
        Tokens.Clear();
        foreach (AdminRegistrationToken token in tokens) Tokens.Add(new AdminTokenItem(token.Token, Describe(token)));
    }

    /// <summary>Run a request with the signed-in client, showing it as busy, and tell when it fails.</summary>
    private async Task RunAsync(string what, Func<MatrixClient, Task> action, bool report = true)
    {
        // Logged in only
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        IsBusy = true;
        if (report) Message = "";
        try
        {
            await action(await connection.Session.WhenReady);
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException or TaskCanceledException)
        {
            Message = $"{what} failed: {MainViewModel.CleanError(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Return a report as the list shows it.</summary>
    internal static AdminReportItem Describe(AdminReport report)
    {
        // What was reported, by whom, when
        string target = report.EventId is not null ? "a message" : report.TargetUserId ?? report.RoomId ?? "?";
        string when = DateTimeOffset.FromUnixTimeMilliseconds(report.ReceivedTs).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string? sender = report.EventSummary?.Split(": ", 2)[0] is string first && first.StartsWith('@') ? first : null;
        string details = string.Join("\n", new[] { $"Reason: {report.Reason}", report.EventSummary, report.RoomId is null ? null : $"Room: {report.RoomId}" }.OfType<string>());
        // Return item
        return new AdminReportItem(report, $"{report.UserId} reported {target} · {when}", details, report.TargetUserId ?? sender);
    }

    /// <summary>Return one line about an account.</summary>
    internal static string Describe(AdminAccount account)
    {
        // Name, flags, use
        IEnumerable<string> flags = new[] { account.Admin ? "admin" : null, account.Suspended ? "suspended" : null, account.Locked ? "locked" : null, account.Deactivated ? "deactivated" : null }.OfType<string>();
        // Return line
        List<string?> parts = [account.DisplayName, .. flags, $"{account.Devices ?? 0} devices", $"{account.JoinedRooms ?? 0} rooms"];
        return string.Join(" · ", parts.OfType<string>());
    }

    /// <summary>Return one line about a token.</summary>
    internal static string Describe(AdminRegistrationToken token)
    {
        // Uses, then expiry
        string uses = token.UsesAllowed is int allowed ? $"used {token.Completed} of {allowed}" : $"used {token.Completed}, no limit";
        string expiry = token.ExpiryTime is long ms ? $"until {DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" : "no expiry";
        // Return line
        return $"{uses}, {expiry}";
    }

    /// <summary>Return one line about a device of whois.</summary>
    private static string DescribeDevice(string deviceId, WhoisDevice device)
    {
        // Last connection
        WhoisConnection? last = device.Sessions.SelectMany(s => s.Connections).OrderByDescending(c => c.LastSeen).FirstOrDefault();
        string seen = last?.LastSeen is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "never";
        // Return line
        return $"{device.DisplayName ?? deviceId} ({deviceId}), last seen {seen} from {last?.Ip ?? "unknown"}";
    }
}
