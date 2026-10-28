namespace Uniform.Matrix.Client.Terminal.Ui;

using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Crypto;

// Encryption status, recovery and device verification.
public sealed partial class MainWindow
{
    private readonly HashSet<(string TransactionId, VerificationState State)> shownVerificationStates = [];

    /// <summary>Show this device's keys and the account's cross-signing, backup and recovery state, offering what is missing.</summary>
    private async Task ShowEncryptionAsync()
    {
        // Assert encryption
        if (!crypto.Available || crypto.IdentityKeys is not IdentityKeys keys)
        {
            Dialogs.Error(app, "Encryption", $"End-to-end encryption is not available.\n\n{crypto.UnavailableReason}");
            return;
        }

        // Account state from the server
        ShowStatus("Checking encryption…");
        SecurityStatus security;
        try
        {
            security = await crypto.GetSecurityStatusAsync();
        }
        catch (Exception ex)
        {
            RestoreStatus();
            Dialogs.Error(app, "Encryption", ex.Message);
            return;
        }
        RestoreStatus();

        // Describe, then offer the next step
        string text = string.Join('\n',
            $"Device:         {crypto.DeviceId}",
            $"Fingerprint:    {CryptoService.FormatFingerprint(keys.Ed25519)}",
            "",
            $"Cross-signing:  {(!security.CrossSigningSetUp ? "not set up" : security.IdentityTrusted ? "set up and trusted here" : "set up, not yet trusted here")}",
            $"This device:    {(security.ThisDeviceCrossSigned ? "verified (cross-signed)" : "not verified")}",
            $"Key backup:     {(security.BackupVersion is null ? "none" : security.BackupUsable ? $"on (version {security.BackupVersion})" : $"version {security.BackupVersion}, needs the recovery key")}",
            $"Recovery key:   {(security.SecretStorageSetUp ? "set up" : "not set up")}");
        List<(string Label, Action Run)> actions = [("_Close", () => { })];
        if (!security.CrossSigningSetUp) actions.Add(("Set _up recovery", () => _ = SetUpRecoveryAsync()));
        if (security.SecretStorageSetUp && (!security.HasPrivateKeys || !security.BackupUsable)) actions.Add(("_Recover", RecoverWithKey));
        if (security.CrossSigningSetUp) actions.Add(("_Verify this device", () => VerifyUser(ownUserId)));

        // Follow-up dialogs run after this one closed
        int? choice = Dialogs.Choose(app, "Encryption", text, [.. actions.Select(a => a.Label)]);
        if (choice is int index && index > 0) app.Invoke(actions[index].Run);
    }

    /// <summary>Create cross-signing keys, a key backup and a recovery key after confirmation, asking for the password when the server wants it.</summary>
    private async Task SetUpRecoveryAsync()
    {
        // Confirm
        if (!Dialogs.Confirm(app, "Set up recovery", "Create cross-signing keys, a key backup and a recovery key?\n\nWith the recovery key, a new login can verify itself and read old messages. Keep it safe, like a password.")) return;

        // Set up; the server may want the password first
        ShowStatus("Setting up recovery…");
        string? recoveryKey = null;
        try
        {
            try
            {
                recoveryKey = await crypto.SetUpRecoveryAsync(password: null);
            }
            catch (UserInteractiveAuthException ex) when (ex.AcceptsPassword)
            {
                string? password = Dialogs.Ask(app, "Confirm", $"Password for {ownUserId}:", secret: true);
                if (password is null) return;
                recoveryKey = await crypto.SetUpRecoveryAsync(password);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Setting up recovery failed");
            Dialogs.Error(app, "Set up recovery", ex.Message);
            return;
        }
        finally
        {
            RestoreStatus();
        }

        // Show the key once
        Dialogs.ShowCopyable(app, "Recovery key", "Write down or copy this recovery key now and keep it safe. It is not shown again.", recoveryKey);
    }

    /// <summary>Ask for the recovery key, restores cross-signing and the key backup, and decrypt stored messages with the restored keys.</summary>
    private void RecoverWithKey()
    {
        // Assert encryption and ask
        if (!crypto.Available)
        {
            Dialogs.Error(app, "Recover", $"Encryption is not available on this device.\n\n{crypto.UnavailableReason}");
            return;
        }
        string? recoveryKey = Dialogs.Ask(app, "Recover", "Recovery key (or passphrase):");
        if (recoveryKey is null) return;

        // Restore off the UI thread, then decrypt what waited for keys
        ShowStatus("Recovering keys…");
        _ = RunSafelyAsync("Recovering", async _ =>
        {
            RecoveryResult result = await crypto.RecoverAsync(recoveryKey);
            await processor.DecryptWithReceivedKeysAsync();
            app.Invoke(() =>
            {
                RestoreStatus();
                Dialogs.Info(app, "Recovered", string.Join('\n',
                    result.CrossSigningRestored ? "Cross-signing restored; this device is now verified." : "Cross-signing keys were not in secret storage.",
                    result.BackupRestored ? $"Key backup restored: {result.KeysImported} room keys imported." : "No usable key backup was found."));
            });
        });
    }

    /// <summary>Start verifying <paramref name="userId"/>: our own other devices, or another user's devices.</summary>
    private void VerifyUser(string userId)
    {
        // Assert encryption and a user ID
        if (!crypto.Available)
        {
            Dialogs.Error(app, "Verify", $"Encryption is not available on this device.\n\n{crypto.UnavailableReason}");
            return;
        }
        if (!userId.StartsWith('@'))
        {
            Dialogs.Error(app, "Verify", "Give a user ID like @name:server.");
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
        if (deviceId is not null && !crypto.GetSignedDevices(ownUserId).Any(d => d.DeviceId == deviceId)) return $"Neither this device nor {deviceId} is trusted under your current identity, so neither can vouch for the other.\n\nVerify this device with one that is trusted{(trusted.Count > 0 ? $": {string.Join(", ", trusted)}" : "")}, or use your recovery key.";
        // Return none, or that no device can
        return deviceId is null && trusted.Count == 0 ? "None of your devices is trusted under your current identity, so none can vouch for this one.\n\nUse your recovery key, or reset your identity on a device you keep." : null;
    }

    /// <summary>Send a verification request; the other device must accept it.</summary>
    private async Task StartVerificationAsync(string userId, string? deviceId)
    {
        // Our own devices only work when one side is trusted
        if (userId == ownUserId && crypto.Available && await CannotVerifyOwnAsync(deviceId) is string reason)
        {
            Dialogs.Error(app, "Verify", reason);
            return;
        }

        // Request
        ShowStatus("Sending verification request…");
        await RunSafelyAsync("Verification", async _ =>
        {
            await crypto.RequestVerificationAsync(userId, deviceId);
            SecurityStatus security = await crypto.GetSecurityStatusAsync();
            string where = userId != ownUserId ? userId : security.CrossSigningSetUp && !security.ThisDeviceCrossSigned ? "one of your trusted devices" : "your other device";
            app.Invoke(() => ShowStatus($"Verification requested; accept it on {where}."));
        });
    }

    /// <summary>Follow a verification as it changes: asks to accept incoming requests, shows emoji to compare, and report the outcome.</summary>
    private void OnVerificationChanged(Verification verification, VerificationState state)
    {
        // Each state once
        if (!shownVerificationStates.Add((verification.TransactionId, state))) return;
        string who = verification.OtherUserId == ownUserId ? $"your device {verification.OtherDeviceId}" : $"{verification.OtherUserId} ({verification.OtherDeviceId})";

        // By state
        switch (state)
        {
            case VerificationState.Requested when !verification.WeRequested:
                bool accept = Dialogs.Confirm(app, "Verification request", $"{who} wants to verify this device by comparing emoji.\n\nAccept?");
                _ = RunSafelyAsync("Verification", _ => accept ? crypto.AcceptVerificationAsync(verification) : crypto.CancelVerificationAsync(verification));
                break;
            case VerificationState.ShowingSas:
                CompareEmoji(verification, who);
                break;
            case VerificationState.Confirmed:
                ShowStatus($"Waiting for {who} to confirm…");
                break;
            case VerificationState.Done:
                ShowStatus($"Verified {who}");
                Dialogs.Info(app, "Verified", $"{who} is verified.");
                break;
            case VerificationState.Cancelled:
                ShowStatus($"Verification with {who} cancelled: {verification.CancelReason}");
                if (verification.CancelCode is not ("m.user" or "m.accepted")) Dialogs.Error(app, "Verification cancelled", verification.CancelReason ?? "The verification was cancelled.");
                break;
        }
    }

    /// <summary>Show the emoji and ask whether the other device shows the same.</summary>
    private void CompareEmoji(Verification verification, string who)
    {
        // Emoji with names, and the numbers as an alternative
        string emoji = string.Join("   ", verification.Emoji.Select(e => $"{e.Symbol} {e.Name}"));
        string numbers = string.Join(" ", verification.Decimals);
        string text = $"Compare with {who}:\n\n{emoji}\n\nor the numbers {numbers}\n\nDo they show the same, in the same order?";

        // Confirm, deny or stop
        int? choice = Dialogs.Choose(app, "Compare emoji", text, ["They _match", "They _don't match", "_Cancel"]);
        Task action = choice switch
        {
            0 => crypto.ConfirmVerificationAsync(verification),
            1 => crypto.CancelVerificationAsync(verification, "m.mismatched_sas"),
            _ => crypto.CancelVerificationAsync(verification),
        };
        _ = RunSafelyAsync("Verification", _ => action);
    }

    /// <summary>Return a short label of a device's trust for lists.</summary>
    private static string TrustLabel(DeviceTrust? trust) => trust switch
    {
        DeviceTrust.ThisDevice => "this device",
        DeviceTrust.Verified => "verified",
        DeviceTrust.CrossSigned => "cross-signed",
        DeviceTrust.Unverified => "unverified",
        _ => "no keys",
    };
}
