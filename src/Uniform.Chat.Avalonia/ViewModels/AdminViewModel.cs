namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Chat.Avalonia.Localization;

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
    public string SuspendText => SelectedAccount?.Account.Suspended == true ? Strings.EndSuspension : Strings.Suspend;
    /// <summary>Text of the lock button of the chosen account.</summary>
    public string LockText => SelectedAccount?.Account.Locked == true ? Strings.Unlock : Strings.Lock;
    /// <summary>Text of the admin button of the chosen account.</summary>
    public string AdminText => SelectedAccount?.Account.Admin == true ? Strings.TakeAdminRights : Strings.MakeAdmin;

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
        AdminSection.Reports => RunAsync(Strings.LoadingReports, LoadReportsAsync),
        AdminSection.Accounts => RunAsync(Strings.LoadingAccounts, LoadAccountsAsync),
        _ => RunAsync(Strings.LoadingTokens, LoadTokensAsync),
    };

    /// <summary>Delete a report.</summary>
    public Task DeleteReportAsync(AdminReportItem item) => RunAsync(Strings.DeletingReport, async client =>
    {
        // Delete, then list again
        await client.DeleteReportAsync(item.Report.Id);
        await LoadReportsAsync(client);
        Message = Strings.ReportDeleted;
    });

    /// <summary>Suspend the reported user.</summary>
    public Task SuspendReportedAsync(AdminReportItem item) => item.ReportedUser is not string userId ? Task.CompletedTask : RunAsync(Strings.Suspending, async client =>
    {
        // Suspend, then tell
        await client.SetSuspendedAsync(userId, true);
        Message = Translations.Format(Strings.SuspendedUser, userId);
    });

    /// <summary>Ask to shut down the reported room, which cannot be undone.</summary>
    public void ShutDownRoom(AdminReportItem item)
    {
        // Confirmed later
        if (item.Report.RoomId is not string roomId) return;
        Ask(Translations.Format(Strings.ShutDownConfirm, roomId), () => RunAsync(Strings.ShuttingRoomDown, async client =>
        {
            AdminRoomShutdown done = await client.ShutdownRoomAsync(roomId, block: true, Strings.RoomShutDownReason);
            Message = Translations.Plural(nameof(Strings.ShutDownDone_other), done.KickedUsers.Count, roomId, done.KickedUsers.Count);
        }));
    }

    /// <summary>Find accounts by <see cref="Search"/>.</summary>
    [RelayCommand]
    private Task SearchAccountsAsync() => RunAsync(Strings.LoadingAccounts, LoadAccountsAsync);

    /// <summary>Show the actions of an account, and its devices.</summary>
    public Task SelectAccountAsync(AdminAccountItem item)
    {
        // Chosen, then whois
        SelectedAccount = item;
        Devices = "";
        NewPassword = "";
        NoticeText = "";
        // Done
        return RunAsync(Strings.LookingUpDevices, async client =>
        {
            WhoisResponse whois = await client.WhoisAsync(item.Name);
            Devices = whois.Devices.Count == 0 ? Strings.NoDevicesSignedIn : string.Join("\n", whois.Devices.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => DescribeDevice(d.Key, d.Value)));
        }, report: false);
    }

    /// <summary>Close the chosen account's actions.</summary>
    [RelayCommand]
    private void CloseAccount() => SelectedAccount = null;

    /// <summary>Suspend the chosen account, or end the suspension.</summary>
    [RelayCommand]
    private Task ToggleSuspendedAsync() => ChangeAccountAsync(Strings.Suspending, (client, account) => client.SetSuspendedAsync(account.Name, !account.Suspended), account => Translations.Format(account.Suspended ? Strings.EndedSuspension : Strings.SuspendedUser, account.Name));

    /// <summary>Lock the chosen account, or unlock it.</summary>
    [RelayCommand]
    private Task ToggleLockedAsync() => ChangeAccountAsync(Strings.Locking, (client, account) => client.SetLockedAsync(account.Name, !account.Locked), account => Translations.Format(account.Locked ? Strings.UnlockedUser : Strings.LockedUser, account.Name));

    /// <summary>Give or take the chosen account's admin rights.</summary>
    [RelayCommand]
    private Task ToggleAdminAsync() => ChangeAccountAsync(Strings.ChangingAdminRights, (client, account) => client.SetServerAdminAsync(account.Name, !account.Admin), account => Translations.Format(account.Admin ? Strings.NoLongerAdmin : Strings.IsAdmin, account.Name));

    /// <summary>Set <see cref="NewPassword"/> for the chosen account, logging out its devices.</summary>
    [RelayCommand]
    private Task ResetPasswordAsync() => NewPassword.Length == 0 ? Task.CompletedTask : ChangeAccountAsync(Strings.ResettingPassword, (client, account) => client.ResetPasswordAsync(account.Name, NewPassword), account =>
    {
        // Forget the password, then tell
        NewPassword = "";
        return Translations.Format(Strings.PasswordReset, account.Name);
    });

    /// <summary>Send <see cref="NoticeText"/> to the chosen account as a server notice.</summary>
    [RelayCommand]
    private Task SendNoticeAsync() => NoticeText.Trim().Length == 0 ? Task.CompletedTask : ChangeAccountAsync(Strings.SendingNotice, (client, account) => client.SendServerNoticeAsync(account.Name, NoticeText.Trim()), account =>
    {
        // Clear, then tell
        NoticeText = "";
        return Translations.Format(Strings.NoticeSent, account.Name);
    });

    /// <summary>Ask to deactivate the chosen account, which cannot be undone.</summary>
    [RelayCommand]
    private void Deactivate()
    {
        // Confirmed later
        if (SelectedAccount?.Account is not AdminAccount account) return;
        Ask(Translations.Format(Strings.DeactivateConfirm, account.Name), () => ChangeAccountAsync(Strings.Deactivating, (client, _) => client.DeactivateUserAsync(account.Name), _ => Translations.Format(Strings.Deactivated, account.Name)));
    }

    /// <summary>Make a registration token with <see cref="NewTokenUses"/> and <see cref="NewTokenDays"/>.</summary>
    [RelayCommand]
    private Task CreateTokenAsync() => RunAsync(Strings.CreatingToken, async client =>
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
    public Task DeleteTokenAsync(AdminTokenItem item) => RunAsync(Strings.DeletingToken, async client =>
    {
        // Delete, then list again
        await client.DeleteRegistrationTokenAsync(item.Token);
        if (CreatedToken == item.Token) CreatedToken = "";
        await LoadTokensAsync(client);
        Message = Strings.TokenDeleted;
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
        if (Reports.Count == 0) Message = Strings.NoReports;
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
            Message = Translations.Format(Strings.ActionFailed, what, MainViewModel.CleanError(exception.Message));
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
        string target = report.EventId is not null ? Strings.AMessage : report.TargetUserId ?? report.RoomId ?? "?";
        string when = DateTimeOffset.FromUnixTimeMilliseconds(report.ReceivedTs).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string? sender = report.EventSummary?.Split(": ", 2)[0] is string first && first.StartsWith('@') ? first : null;
        string details = string.Join("\n", new[] { Translations.Format(Strings.ReasonLine, report.Reason), report.EventSummary, report.RoomId is null ? null : Translations.Format(Strings.RoomLine, report.RoomId) }.OfType<string>());
        // Return item
        return new AdminReportItem(report, Translations.Format(Strings.ReportedLine, report.UserId, target, when), details, report.TargetUserId ?? sender);
    }

    /// <summary>Return one line about an account.</summary>
    internal static string Describe(AdminAccount account)
    {
        // Name, flags, use
        IEnumerable<string> flags = new[] { account.Admin ? Strings.FlagAdmin : null, account.Suspended ? Strings.FlagSuspended : null, account.Locked ? Strings.FlagLocked : null, account.Deactivated ? Strings.FlagDeactivated : null }.OfType<string>();
        // Return line
        List<string?> parts = [account.DisplayName, .. flags, Translations.Plural(nameof(Strings.DeviceCount_other), account.Devices ?? 0, account.Devices ?? 0), Translations.Plural(nameof(Strings.RoomCount_other), account.JoinedRooms ?? 0, account.JoinedRooms ?? 0)];
        return string.Join(" · ", parts.OfType<string>());
    }

    /// <summary>Return one line about a token.</summary>
    internal static string Describe(AdminRegistrationToken token)
    {
        // Uses, then expiry
        string uses = token.UsesAllowed is int allowed ? Translations.Format(Strings.TokenUsedOf, token.Completed, allowed) : Translations.Format(Strings.TokenUsedNoLimit, token.Completed);
        string expiry = token.ExpiryTime is long ms ? Translations.Format(Strings.TokenUntil, DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : Strings.NoExpiry;
        // Return line
        return $"{uses}, {expiry}";
    }

    /// <summary>Return one line about a device of whois.</summary>
    private static string DescribeDevice(string deviceId, WhoisDevice device)
    {
        // Last connection
        WhoisConnection? last = device.Sessions.SelectMany(s => s.Connections).OrderByDescending(c => c.LastSeen).FirstOrDefault();
        string seen = last?.LastSeen is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : Strings.Never;
        // Return line
        return Translations.Format(Strings.DeviceLine, device.DisplayName ?? deviceId, deviceId, seen, last?.Ip ?? Strings.Unknown);
    }
}
