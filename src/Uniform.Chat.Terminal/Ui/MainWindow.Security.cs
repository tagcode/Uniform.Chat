namespace Uniform.Chat.Terminal.Ui;

using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Chat.Terminal.Localization;

// Encryption status, recovery and device verification.
public sealed partial class MainWindow
{
    private readonly HashSet<(string TransactionId, VerificationState State)> shownVerificationStates = [];
    private bool securityChecked;

    /// <summary>Offer the encryption window when the account has no cross-signing yet, as a new account, or this device is not signed with it; otherwise others see this device as unverified.</summary>
    private async Task SuggestSecurityAsync()
    {
        // The account's state, off the UI thread
        if (!crypto.Available) return;
        SecurityStatus status;
        try
        {
            status = await crypto.GetSecurityStatusAsync();
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException or InvalidOperationException)
        {
            logger.LogInformation("Security status not available: {Error}", ex.Describe());
            return;
        }

        // What is missing, offered once
        string? missing = !status.CrossSigningSetUp ? Strings.NoRecoveryYet
            : !status.ThisDeviceCrossSigned ? Strings.DeviceNotVerifiedYet
            : null;
        if (missing is null) return;
        // Done
        app.Invoke(() => { if (Dialogs.Confirm(app, Strings.Encryption, Texts.Format(Strings.OpenEncryptionQuestion, missing))) _ = ShowEncryptionAsync(); });
    }

    /// <summary>Show this device's keys and the account's cross-signing, backup and recovery state, offering what is missing.</summary>
    private async Task ShowEncryptionAsync()
    {
        // Assert encryption
        if (!crypto.Available || crypto.IdentityKeys is not IdentityKeys keys)
        {
            Dialogs.Error(app, Strings.Encryption, Texts.Format(Strings.E2eeUnavailable, crypto.UnavailableReason));
            return;
        }

        // Account state from the server
        ShowStatus(Strings.CheckingEncryption);
        SecurityStatus security;
        try
        {
            security = await crypto.GetSecurityStatusAsync();
        }
        catch (Exception ex)
        {
            RestoreStatus();
            Dialogs.Error(app, Strings.Encryption, ex.Message);
            return;
        }
        RestoreStatus();

        // Describe, then offer the next step
        string text = string.Join('\n',
            Texts.Format(Strings.StatusDevice, crypto.DeviceId),
            Texts.Format(Strings.StatusFingerprint, CryptoService.FormatFingerprint(keys.Ed25519)),
            "",
            Texts.Format(Strings.StatusCrossSigning, (!security.CrossSigningSetUp ? Strings.NotSetUp : security.IdentityTrusted ? Strings.SetUpAndTrusted : Strings.SetUpNotTrusted)),
            Texts.Format(Strings.StatusThisDevice, (security.ThisDeviceCrossSigned ? Strings.VerifiedCrossSigned : Strings.NotVerified)),
            Texts.Format(Strings.StatusKeyBackup, (security.BackupVersion is null ? Strings.None : security.BackupUsable ? Texts.Format(Strings.BackupOn, security.BackupVersion) : Texts.Format(Strings.BackupNeedsKey, security.BackupVersion))),
            Texts.Format(Strings.StatusRecoveryKey, (security.SecretStorageSetUp ? Strings.SetUp : Strings.NotSetUp)));
        List<(string Label, Action Run)> actions = [(Strings.ButtonClose, () => { })];
        if (!security.CrossSigningSetUp) actions.Add((Strings.ButtonSetUpRecovery, () => _ = SetUpRecoveryAsync()));
        if (security.SecretStorageSetUp && (!security.HasPrivateKeys || !security.BackupUsable)) actions.Add((Strings.ButtonRecover, RecoverWithKey));
        if (security.CrossSigningSetUp) actions.Add((Strings.ButtonVerifyDevice, () => VerifyUser(ownUserId)));

        // Follow-up dialogs run after this one closed
        int? choice = Dialogs.Choose(app, Strings.Encryption, text, [.. actions.Select(a => a.Label)]);
        if (choice is int index && index > 0) app.Invoke(actions[index].Run);
    }

    /// <summary>Create cross-signing keys, a key backup and a recovery key after confirmation, asking for the password when the server wants it.</summary>
    private async Task SetUpRecoveryAsync()
    {
        // Confirm
        if (!Dialogs.Confirm(app, Strings.SetUpRecovery, Strings.SetUpRecoveryQuestion)) return;

        // Set up; the server may want the password first
        ShowStatus(Strings.SettingUpRecovery);
        string? recoveryKey = null;
        try
        {
            try
            {
                recoveryKey = await crypto.SetUpRecoveryAsync(password: null);
            }
            catch (UserInteractiveAuthException ex) when (ex.AcceptsPassword)
            {
                string? password = Dialogs.Ask(app, Strings.Confirm, Texts.Format(Strings.PasswordFor, ownUserId), secret: true);
                if (password is null) return;
                recoveryKey = await crypto.SetUpRecoveryAsync(password);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Setting up recovery failed");
            Dialogs.Error(app, Strings.SetUpRecovery, ex.Message);
            return;
        }
        finally
        {
            RestoreStatus();
        }

        // Show the key once
        Dialogs.ShowCopyable(app, Strings.RecoveryKey, Strings.WriteDownRecoveryKey, recoveryKey);
    }

    /// <summary>Ask for the recovery key, restores cross-signing and the key backup, and decrypt stored messages with the restored keys.</summary>
    private void RecoverWithKey()
    {
        // Assert encryption and ask
        if (!crypto.Available)
        {
            Dialogs.Error(app, Strings.Recover, Texts.Format(Strings.EncryptionUnavailable, crypto.UnavailableReason));
            return;
        }
        string? recoveryKey = Dialogs.Ask(app, Strings.Recover, Strings.RecoveryKeyPrompt);
        if (recoveryKey is null) return;

        // Restore off the UI thread, then decrypt what waited for keys
        ShowStatus(Strings.RecoveringKeys);
        _ = RunSafelyAsync(Strings.Recovering, async _ =>
        {
            RecoveryResult result = await crypto.RecoverAsync(recoveryKey);
            await processor.DecryptWithReceivedKeysAsync();
            app.Invoke(() =>
            {
                RestoreStatus();
                Dialogs.Info(app, Strings.Recovered, string.Join('\n',
                    result.CrossSigningRestored ? Strings.CrossSigningRestored : Strings.CrossSigningNotStored,
                    result.BackupRestored ? Texts.Format(Strings.BackupRestored, result.KeysImported) : Strings.NoUsableBackup));
            });
        });
    }

    /// <summary>Start verifying <paramref name="userId"/>: our own other devices, or another user's devices.</summary>
    private void VerifyUser(string userId)
    {
        // Assert encryption and a user ID
        if (!crypto.Available)
        {
            Dialogs.Error(app, Strings.Verify, Texts.Format(Strings.EncryptionUnavailable, crypto.UnavailableReason));
            return;
        }
        if (!userId.StartsWith('@'))
        {
            Dialogs.Error(app, Strings.Verify, Strings.GiveUserId);
            return;
        }
        _ = StartVerificationAsync(userId, deviceId: null);
    }

    /// <summary>Return the trust label of each of our devices: <see cref="WarningListSource.NotTrusted"/> for devices the account's current identity has not signed, this one too, else how they are trusted.</summary>
    private async Task<Func<string, string>> DeviceTrustLabelsAsync()
    {
        // Without an identity there is nothing to be trusted under
        SecurityStatus security = await crypto.GetSecurityStatusAsync();
        HashSet<string> signed = [.. crypto.GetSignedDevices(ownUserId).Select(d => d.DeviceId)];
        string? thisDevice = crypto.DeviceId;
        // Return labels
        return deviceId =>
        {
            if (security.CrossSigningSetUp && !(deviceId == thisDevice ? security.ThisDeviceCrossSigned : signed.Contains(deviceId))) return WarningListSource.NotTrusted;
            return TrustLabel(crypto.GetDeviceTrust(ownUserId, deviceId));
        };
    }

    /// <summary>Return why verifying our own devices from here cannot work, or null when it can: a device can only be vouched for by one the account's current identity has signed.</summary>
    /// <param name="deviceId">The other device, or null for all of them.</param>
    private async Task<string?> CannotVerifyOwnAsync(string? deviceId)
    {
        // Fine when this device is trusted, or there is no identity yet
        SecurityStatus security = await crypto.GetSecurityStatusAsync();
        if (!security.CrossSigningSetUp || security.ThisDeviceCrossSigned) return null;
        List<string> trusted = [.. crypto.GetSignedDevices(ownUserId).Where(d => d.DeviceId != crypto.DeviceId).Select(d => d.DisplayName is { Length: > 0 } name ? $"{name} ({d.DeviceId})" : d.DeviceId)];

        // A given device must vouch for this one; any trusted device will do otherwise
        if (deviceId is not null && !crypto.GetSignedDevices(ownUserId).Any(d => d.DeviceId == deviceId)) return Texts.Format(Strings.NeitherTrusted, deviceId, (trusted.Count > 0 ? Texts.Format(Strings.TrustedList, string.Join(", ", trusted)) : ""));
        // Return none, or that no device can
        return deviceId is null && trusted.Count == 0 ? Strings.NoneTrusted : null;
    }

    /// <summary>Send a verification request; the other device must accept it.</summary>
    private async Task StartVerificationAsync(string userId, string? deviceId)
    {
        // Our own devices only work when one side is trusted
        if (userId == ownUserId && crypto.Available && await CannotVerifyOwnAsync(deviceId) is string reason)
        {
            Dialogs.Error(app, Strings.Verify, reason);
            return;
        }

        // Request
        ShowStatus(Strings.SendingVerificationRequest);
        await RunSafelyAsync(Strings.Verification, async _ =>
        {
            await crypto.RequestVerificationAsync(userId, deviceId);
            SecurityStatus security = await crypto.GetSecurityStatusAsync();
            string where = userId != ownUserId ? userId : security.CrossSigningSetUp && !security.ThisDeviceCrossSigned ? Strings.OneOfTrustedDevices : Strings.YourOtherDevice;
            app.Invoke(() => ShowStatus(Texts.Format(Strings.VerificationRequested, where)));
        });
    }

    /// <summary>Follow a verification as it changes: asks to accept incoming requests, shows emoji to compare, and report the outcome.</summary>
    private void OnVerificationChanged(Verification verification, VerificationState state)
    {
        // Each state once
        if (!shownVerificationStates.Add((verification.TransactionId, state))) return;
        string who = verification.OtherUserId == ownUserId ? Texts.Format(Strings.YourDevice, verification.OtherDeviceId) : $"{verification.OtherUserId} ({verification.OtherDeviceId})";

        // By state
        switch (state)
        {
            case VerificationState.Requested when !verification.WeRequested:
                bool accept = Dialogs.Confirm(app, Strings.VerificationRequest, Texts.Format(Strings.WantsToVerify, who));
                _ = RunSafelyAsync(Strings.Verification, _ => accept ? crypto.AcceptVerificationAsync(verification) : crypto.CancelVerificationAsync(verification));
                break;
            case VerificationState.ShowingSas:
                CompareEmoji(verification, who);
                break;
            case VerificationState.Confirmed:
                ShowStatus(Texts.Format(Strings.WaitingToConfirm, who));
                break;
            case VerificationState.Done:
                ShowStatus(Texts.Format(Strings.VerifiedWho, who));
                Dialogs.Info(app, "Verified", Texts.Format(Strings.IsVerified, who));
                break;
            case VerificationState.Cancelled:
                ShowStatus(Texts.Format(Strings.VerificationCancelledWith, who, verification.CancelReason));
                if (verification.CancelCode is not ("m.user" or "m.accepted")) Dialogs.Error(app, Strings.VerificationCancelled, verification.CancelReason ?? Strings.TheVerificationCancelled);
                break;
        }
    }

    /// <summary>Show the emoji and ask whether the other device shows the same.</summary>
    private void CompareEmoji(Verification verification, string who)
    {
        // Emoji with names, and the numbers as an alternative
        string emoji = string.Join("   ", verification.Emoji.Select(e => $"{e.Symbol} {e.Name}"));
        string numbers = string.Join(" ", verification.Decimals);
        string text = Texts.Format(Strings.CompareWith, who, emoji, numbers);

        // Confirm, deny or stop
        int? choice = Dialogs.Choose(app, Strings.CompareEmoji, text, [Strings.ButtonTheyMatch, Strings.ButtonTheyDontMatch, Strings.ButtonCancel]);
        Task action = choice switch
        {
            0 => crypto.ConfirmVerificationAsync(verification),
            1 => crypto.CancelVerificationAsync(verification, "m.mismatched_sas"),
            _ => crypto.CancelVerificationAsync(verification),
        };
        _ = RunSafelyAsync(Strings.Verification, _ => action);
    }

    /// <summary>Return a short label of a device's trust for lists.</summary>
    private static string TrustLabel(DeviceTrust? trust) => trust switch
    {
        DeviceTrust.ThisDevice => Strings.TrustThisDevice,
        DeviceTrust.Verified => Strings.TrustVerified,
        DeviceTrust.CrossSigned => Strings.TrustCrossSigned,
        DeviceTrust.Unverified => Strings.TrustUnverified,
        _ => Strings.TrustNoKeys,
    };
}
