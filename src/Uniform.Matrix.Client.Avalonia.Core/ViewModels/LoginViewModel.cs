namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Protocol.Core;

/// <summary>The sign-in form: user ID, password, and the homeserver address for servers without discovery.</summary>
public sealed partial class LoginViewModel(Func<SignIn, Task> signIn) : ViewModelBase
{
    /// <summary>Full user ID, e.g. <c>@alice:matrix.org</c>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string UserId { get; set; } = "";

    /// <summary>Password.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = "";

    /// <summary>Homeserver address; found from the user ID when empty.</summary>
    [ObservableProperty]
    public partial string Homeserver { get; set; } = "";

    /// <summary>Why the last attempt failed; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = "";

    /// <summary>Whether signing in is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether there is an error to show.</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>Sign in with the form's values.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        // Assert a user ID
        string userId = UserId.Trim();
        if (!Uniform.Matrix.Protocol.Core.UserId.TryParse(userId, out _))
        {
            ErrorMessage = $"\"{userId}\" is not a Matrix user ID; it looks like @alice:matrix.org.";
            return;
        }

        // Sign in; the caller reports failures in ErrorMessage
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            await signIn(new SignIn { UserId = userId, Password = Password, Homeserver = Homeserver.Trim() is { Length: > 0 } address ? address : null });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Return whether the form can be sent.</summary>
    private bool CanSignIn() => !IsBusy && UserId.Trim().Length > 0 && Password.Length > 0;
}
