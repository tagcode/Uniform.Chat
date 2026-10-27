namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;

/// <summary>Encryption status with recovery set-up, recovery and verification.</summary>
public partial class SecurityView : UserControl
{
    /// <summary>Create the view.</summary>
    public SecurityView() => InitializeComponent();

    /// <summary>Ask the device of the clicked row to verify.</summary>
    private void OnVerifyDeviceClick(object? sender, RoutedEventArgs e)
    {
        // The row's device
        if (DataContext is SecurityViewModel security && (sender as Control)?.DataContext is DeviceItem device) _ = security.VerifyDeviceAsync(device);
    }

    /// <summary>Ask for the password to sign out the device of the clicked row.</summary>
    private void OnSignOutDeviceClick(object? sender, RoutedEventArgs e)
    {
        // The row's device
        if (DataContext is SecurityViewModel security && (sender as Control)?.DataContext is DeviceItem device) security.StartSignOut(device);
    }

    /// <summary>Copy the new recovery key.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        // Assert a key and a clipboard
        if (DataContext is not SecurityViewModel { HasNewRecoveryKey: true } security || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        try
        {
            await clipboard.SetTextAsync(security.NewRecoveryKey);
            security.Message = "Recovery key copied.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            security.Message = $"Copying failed: {exception.Message}";
        }
    }
}
