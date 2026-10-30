namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>The sign-in form, which also creates accounts.</summary>
public partial class LoginView : UserControl
{
    /// <summary>Create the view.</summary>
    public LoginView() => InitializeComponent();

    /// <summary>Focus the first empty field.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        // User ID, or the password when the user ID is remembered
        base.OnLoaded(e);
        if (string.IsNullOrEmpty(UserIdBox.Text)) UserIdBox.Focus();
        else PasswordBox.Focus();
    }

    /// <summary>Open the policy address in the button's <see cref="Control.Tag"/> in the system's browser.</summary>
    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        // Assert an address and a launcher
        if ((sender as Control)?.Tag is not string address || !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || TopLevel.GetTopLevel(this) is not TopLevel top) return;
        try
        {
            if (!await top.Launcher.LaunchUriAsync(uri) && DataContext is LoginViewModel model) model.ErrorMessage = $"No app opens {address} here.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            if (DataContext is LoginViewModel model) model.ErrorMessage = $"Opening {address} failed: {exception.Message}";
        }
    }
}
