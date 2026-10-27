namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia.Controls;
using global::Avalonia.Interactivity;

/// <summary>The sign-in form.</summary>
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
}
