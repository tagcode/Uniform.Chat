namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>One labelled line of the security status.</summary>
/// <param name="Label">What the line is about, e.g. <c>Key backup</c>.</param>
/// <param name="Value">Its state, e.g. <c>On</c>.</param>
public sealed record StatusLine(string Label, string Value);

/// <summary>One of the user's devices (sessions).</summary>
/// <param name="DeviceId">Device ID.</param>
/// <param name="Name">Display name, or the ID.</param>
/// <param name="Details">Trust and last use, e.g. <c>Verified · last seen 26.10.2026</c>.</param>
/// <param name="IsThisDevice">Whether it is the device running this app.</param>
/// <param name="CanVerify">Whether it has keys but is not verified yet.</param>
public sealed record DeviceItem(string DeviceId, string Name, string Details, bool IsThisDevice, bool CanVerify)
{
    /// <summary>Get whether it can be signed out from here; this device signs out from the account menu.</summary>
    public bool CanSignOut => !IsThisDevice;
}

/// <summary>Encryption status of this device and the account, with what is missing: set up recovery, recover with a key, or verify with another device; also the blocked users, and deleting the account.</summary>
public sealed partial class SecurityViewModel : ViewModelBase
{
    private readonly ClientConnection connection;
    private readonly Func<Task> recovered;
    private readonly Func<Task>? blockingChanged;
    private readonly Func<Task>? accountDeleted;

    /// <summary>Create the panel for <paramref name="connection"/>.</summary>
    /// <param name="connection">The signed-in account.</param>
    /// <param name="recovered">Runs after keys were restored, to show messages again.</param>
    /// <param name="blockingChanged">Runs after a user was unblocked, to show their messages again.</param>
    /// <param name="accountDeleted">Runs after the account was deleted, to return to the sign-in form.</param>
    public SecurityViewModel(ClientConnection connection, Func<Task> recovered, Func<Task>? blockingChanged = null, Func<Task>? accountDeleted = null)
    {
        // Status loads with RefreshAsync
        this.connection = connection;
        this.recovered = recovered;
        this.blockingChanged = blockingChanged;
        this.accountDeleted = accountDeleted;
    }

    /// <summary>Users the account has blocked, by user ID.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedUsers))]
    public partial IReadOnlyList<string> BlockedUsers { get; set; } = [];

    /// <summary>Get whether anyone is blocked.</summary>
    public bool HasBlockedUsers => BlockedUsers.Count > 0;

    /// <summary>Whether deleting the account waits for the password.</summary>
    [ObservableProperty]
    public partial bool IsDeletingAccount { get; set; }

    /// <summary>Status lines: device, fingerprint, cross-signing, verification, backup and recovery.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StatusLine> Lines { get; set; } = [];

    /// <summary>Whether the account has no cross-signing yet, so recovery can be set up.</summary>
    [ObservableProperty]
    public partial bool CanSetUp { get; set; }

    /// <summary>Whether a recovery key would restore something this device lacks.</summary>
    [ObservableProperty]
    public partial bool CanRecover { get; set; }

    /// <summary>Whether another of the user's devices can verify this one.</summary>
    [ObservableProperty]
    public partial bool CanVerify { get; set; }

    /// <summary>Whether work is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpCommand), nameof(RecoverCommand), nameof(VerifyCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether the server wants the account password before setting up recovery.</summary>
    [ObservableProperty]
    public partial bool NeedsPassword { get; set; }

    /// <summary>Account password, when <see cref="NeedsPassword"/>.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>Recovery key or passphrase to recover with.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand))]
    public partial string RecoveryInput { get; set; } = "";

    /// <summary>Recovery key just created, shown once; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewRecoveryKey))]
    public partial string NewRecoveryKey { get; set; } = "";

    /// <summary>Get whether <see cref="NewRecoveryKey"/> is shown.</summary>
    public bool HasNewRecoveryKey => NewRecoveryKey.Length > 0;

    /// <summary>The user's devices, this one first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevices))]
    public partial IReadOnlyList<DeviceItem> Devices { get; set; } = [];

    /// <summary>Get whether <see cref="Devices"/> is shown.</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>Device waiting for the password to be signed out, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningOutDevice))]
    public partial DeviceItem? SigningOut { get; set; }

    /// <summary>Get whether <see cref="SigningOut"/> asks for the password.</summary>
    public bool IsSigningOutDevice => SigningOut is not null;

    /// <summary>Outcome or error of the last action; empty for none.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>Load the status from the server and decide what to offer.</summary>
    public async Task RefreshAsync()
    {
        // Blocked users as the last sync told
        LoadBlockedUsers();

        // Encryption starts shortly after sign-in
        IsBusy = true;
        await connection.Crypto.WhenStarted.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => { }, TaskScheduler.Default);
        IsBusy = false;

        // Assert encryption
        if (!connection.Crypto.Available || connection.Crypto.IdentityKeys is not IdentityKeys keys)
        {
            Lines = [new StatusLine("Encryption", $"Not available: {connection.Crypto.UnavailableReason}")];
            CanSetUp = CanRecover = CanVerify = false;
            return;
        }

        // Account state from the server
        IsBusy = true;
        try
        {
            SecurityStatus status = await connection.Crypto.GetSecurityStatusAsync();
            Lines =
            [
                new StatusLine("Device", connection.Crypto.DeviceId ?? ""),
                new StatusLine("Fingerprint", CryptoService.FormatFingerprint(keys.Ed25519)),
                new StatusLine("Cross-signing", !status.CrossSigningSetUp ? "Not set up" : status.IdentityTrusted ? "Set up and trusted here" : "Set up, not yet trusted here"),
                new StatusLine("This device", status.ThisDeviceCrossSigned ? "Verified" : "Not verified"),
                new StatusLine("Key backup", status.BackupVersion is null ? "None" : status.BackupUsable ? "On" : "Needs the recovery key"),
                new StatusLine("Recovery key", status.SecretStorageSetUp ? "Set up" : "Not set up"),
            ];
            CanSetUp = !status.CrossSigningSetUp;
            CanRecover = status.SecretStorageSetUp && (!status.HasPrivateKeys || !status.BackupUsable);
            CanVerify = status.CrossSigningSetUp && !status.ThisDeviceCrossSigned;
            await LoadDevicesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error tell
            Message = $"Checking encryption failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Create cross-signing keys, a key backup and a recovery key; ask for the password when the server wants it.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SetUpAsync()
    {
        // Password once the server asked for it
        IsBusy = true;
        Message = "";
        try
        {
            NewRecoveryKey = await connection.Crypto.SetUpRecoveryAsync(NeedsPassword ? Password : null);
            NeedsPassword = false;
            Password = "";
        }
        catch (UserInteractiveAuthException exception) when (exception.AcceptsPassword && !NeedsPassword)
        {
            NeedsPassword = true;
            Message = "Enter your account password to confirm.";
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            connection.Logger.LogWarning(exception, "Setting up recovery failed");
            Message = $"Setting up recovery failed: {exception.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Done
        await RefreshAsync();
    }

    /// <summary>Restore cross-signing and the key backup with <see cref="RecoveryInput"/>, then show stored messages again.</summary>
    [RelayCommand(CanExecute = nameof(CanRecoverNow))]
    private async Task RecoverAsync()
    {
        // Restore, then decrypt what waited for keys
        IsBusy = true;
        Message = "";
        try
        {
            RecoveryResult result = await connection.Crypto.RecoverAsync(RecoveryInput.Trim());
            RecoveryInput = "";
            Message = $"{result.KeysImported} message keys restored{(result.CrossSigningRestored ? "; this device is now verified" : "")}.";
            await recovered();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Message = $"Recovery failed: {exception.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Done
        await RefreshAsync();
    }

    /// <summary>Ask the user's other devices to verify this one by comparing emoji.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task VerifyAsync()
    {
        // The request shows in the verification panel as it goes
        Message = "";
        try
        {
            await connection.Crypto.RequestVerificationAsync(connection.UserId, deviceId: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error tell
            Message = $"Verification failed: {exception.Message}";
        }
    }

    /// <summary>Ask one device to verify this one, or to be verified by it.</summary>
    public async Task VerifyDeviceAsync(DeviceItem device)
    {
        // The request shows in the verification panel as it goes
        Message = "";
        try
        {
            await connection.Crypto.RequestVerificationAsync(connection.UserId, device.DeviceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error tell
            Message = $"Verification failed: {exception.Message}";
        }
    }

    /// <summary>Ask for the password to sign <paramref name="device"/> out.</summary>
    public void StartSignOut(DeviceItem device)
    {
        // Not this device; the account menu signs it out
        if (!device.CanSignOut) return;
        Password = "";
        SigningOut = device;
    }

    /// <summary>Sign out <see cref="SigningOut"/> with <see cref="Password"/>, deleting the device on the server.</summary>
    [RelayCommand]
    private async Task ConfirmSignOutAsync()
    {
        // Assert a device and a login
        if (SigningOut is not DeviceItem device || !connection.Session.WhenReady.IsCompletedSuccessfully) return;
        IsBusy = true;
        try
        {
            MatrixClient client = await connection.Session.WhenReady;
            await client.DeleteDevicesAsync([device.DeviceId], Password);
            Message = $"Signed out {device.Name}.";
            SigningOut = null;
            Password = "";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Message = $"Signing out {device.Name} failed: {exception.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Done; errors of the reload show as the message
        await RefreshAsync();
    }

    /// <summary>Keep the device signed in.</summary>
    [RelayCommand]
    private void CancelSignOut()
    {
        // Forget the password too
        SigningOut = null;
        Password = "";
    }

    /// <summary>Unblock <paramref name="userId"/>, so their messages and invites show again.</summary>
    public async Task UnblockAsync(string userId)
    {
        // Assert a login
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        try
        {
            await connection.Processor.SetIgnoredAsync(await connection.Session.WhenReady, userId, ignored: false);
            Message = $"Unblocked {userId}.";
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException)
        {
            // On error tell
            Message = $"Unblocking {userId} failed: {exception.Message}";
            return;
        }
        LoadBlockedUsers();
        if (blockingChanged is not null) await blockingChanged();
    }

    /// <summary>Ask for the password to delete the account.</summary>
    [RelayCommand]
    private void StartDeleteAccount()
    {
        // Confirmation with the password
        Password = "";
        IsDeletingAccount = true;
    }

    /// <summary>Keep the account.</summary>
    [RelayCommand]
    private void CancelDeleteAccount()
    {
        // Forget the password too
        IsDeletingAccount = false;
        Password = "";
    }

    /// <summary>Delete, i.e. deactivate, the account on the server with <see cref="Password"/>, then return to the sign-in form.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAccountAsync()
    {
        // Assert a login
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        IsBusy = true;
        try
        {
            MatrixClient client = await connection.Session.WhenReady;
            await client.DeactivateAccountAsync(Password);
            IsDeletingAccount = false;
            Password = "";
        }
        catch (UserInteractiveAuthException)
        {
            Message = "The password was not accepted.";
            return;
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException)
        {
            Message = $"Deleting the account failed: {exception.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Done; the account is gone, so is the session
        if (accountDeleted is not null) await accountDeleted();
    }

    /// <summary>Fill <see cref="BlockedUsers"/> from the account data the sync brought.</summary>
    private void LoadBlockedUsers() => BlockedUsers = connection.Processor.Read(store => store?.IgnoredUsers.Order(StringComparer.Ordinal).ToList() ?? []);

    /// <summary>Load the account's devices with their trust from the device keys.</summary>
    private async Task LoadDevicesAsync()
    {
        // Devices from the server, keys from the key server
        MatrixClient client = await connection.Session.WhenReady;
        IReadOnlyList<Device> devices = await client.GetDevicesAsync();
        await connection.Crypto.TrackUserAsync(connection.UserId);
        Devices = [.. devices
            .Select(device =>
            {
                DeviceTrust? trust = connection.Crypto.GetDeviceTrust(connection.UserId, device.DeviceId);
                string seen = device.LastSeenTs is long time ? $" · last seen {DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime:d}" : "";
                string label = trust switch
                {
                    DeviceTrust.ThisDevice => "This device",
                    DeviceTrust.Verified or DeviceTrust.CrossSigned => "Verified",
                    DeviceTrust.Unverified => "Not verified",
                    _ => "No encryption keys",
                };
                return new DeviceItem(device.DeviceId, device.DisplayName is { Length: > 0 } name ? name : device.DeviceId, $"{label}{seen}", device.DeviceId == connection.Crypto.DeviceId, trust == DeviceTrust.Unverified);
            })
            .OrderBy(device => device.IsThisDevice ? 0 : 1)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Return whether no work is in progress.</summary>
    private bool IsIdle() => !IsBusy;

    /// <summary>Return whether a recovery key was entered and no work is in progress.</summary>
    private bool CanRecoverNow() => !IsBusy && RecoveryInput.Trim().Length > 0;
}
