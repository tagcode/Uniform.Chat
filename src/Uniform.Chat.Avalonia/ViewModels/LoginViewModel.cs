namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>The sign-in form: user ID, password, and the homeserver address for servers without discovery; it also creates accounts, with the registration token and the server's policies when the server asks for them.</summary>
/// <param name="signIn">Sign in with the form's values; reports failures in <see cref="ErrorMessage"/>.</param>
/// <param name="creator">Creates accounts; null hides account creation.</param>
public sealed partial class LoginViewModel(Func<SignIn, Task> signIn, IAccountCreator? creator = null) : ViewModelBase
{
    /// <summary>Address of the app's user policy, which people accept by signing in.</summary>
    public const string UserPolicyUrl = "https://github.com/tagcode/Uniform.Chat/blob/main/user-docs/Uniform.Chat.Android/user-policy.md";

    /// <summary>Address of the app's privacy policy.</summary>
    public const string PrivacyPolicyUrl = "https://github.com/tagcode/Uniform.Chat/blob/main/user-docs/Uniform.Chat.Android/privacy-policy.md";

    /// <summary>Requirements of the server last asked, until the user ID or address changes.</summary>
    private RegistrationRequirements? requirements;

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

    /// <summary>Whether signing in or creating an account is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether the form creates an account instead of signing in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ActionText), nameof(SwitchText))]
    public partial bool IsRegistering { get; set; }

    /// <summary>Registration token from the server's admin, when <see cref="NeedsToken"/>.</summary>
    [ObservableProperty]
    public partial string RegistrationToken { get; set; } = "";

    /// <summary>Whether the server needs a registration token.</summary>
    [ObservableProperty]
    public partial bool NeedsToken { get; set; }

    /// <summary>Whether the user accepted <see cref="Policies"/>.</summary>
    [ObservableProperty]
    public partial bool AcceptTerms { get; set; }

    /// <summary>The server's policies to accept before registering.</summary>
    public ObservableCollection<TermsPolicy> Policies { get; } = [];

    /// <summary>Whether the server has policies to accept.</summary>
    [ObservableProperty]
    public partial bool HasPolicies { get; set; }

    /// <summary>Whether accounts can be created here.</summary>
    public bool CanRegister => creator is not null;

    /// <summary>Whether there is an error to show.</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>Heading of the form.</summary>
    public string Title => IsRegistering ? "Create a Matrix account" : "Sign in to Matrix";

    /// <summary>Text of the main button.</summary>
    public string ActionText => IsRegistering ? "Create account" : "Sign in";

    /// <summary>Text of the button that switches between signing in and creating an account.</summary>
    public string SwitchText => IsRegistering ? "I have an account: sign in" : "Create an account";

    /// <summary>Forget the server's requirements when the user ID changes.</summary>
    partial void OnUserIdChanged(string value) => ForgetRequirements();

    /// <summary>Forget the server's requirements when the address changes.</summary>
    partial void OnHomeserverChanged(string value) => ForgetRequirements();

    /// <summary>Switch between signing in and creating an account.</summary>
    [RelayCommand]
    private void SwitchMode()
    {
        // The other mode, without the last error
        IsRegistering = !IsRegistering && CanRegister;
        ErrorMessage = "";
    }

    /// <summary>Sign in, or create the account and then sign in, with the form's values.</summary>
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

        // Create the account first when asked; the caller reports sign-in failures in ErrorMessage
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            SignIn form = new() { UserId = userId, Password = Password, Homeserver = Homeserver.Trim() is { Length: > 0 } address ? address : null };
            if (IsRegistering && !await CreateAccountAsync(form)) return;
            await signIn(form);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Create the account of <paramref name="form"/> once the server's requirements are met; return false and set <see cref="ErrorMessage"/> when they are not, or it failed.</summary>
    private async Task<bool> CreateAccountAsync(SignIn form)
    {
        // What the server needs, asked once
        try
        {
            if (requirements is null)
            {
                requirements = await creator!.GetRequirementsAsync(form);
                NeedsToken = requirements.NeedsToken;
                Policies.Clear();
                foreach (TermsPolicy policy in requirements.Policies) Policies.Add(policy);
                HasPolicies = Policies.Count > 0;
            }
            if (!requirements.Supported) return Fail("This server needs a step this app cannot do, such as a captcha or an email. Create the account on the server's website, then sign in here.");
            if (NeedsToken && RegistrationToken.Trim().Length == 0) return Fail("This server needs a registration token from its admin.");
            if (HasPolicies && !AcceptTerms) return Fail("Read and accept the server's policies to continue.");

            // Create, then sign in as usual
            await creator!.CreateAsync(form, RegistrationToken.Trim(), AcceptTerms);
            IsRegistering = false;
            RegistrationToken = "";
            return true;
        }
        catch (UserInteractiveAuthException)
        {
            return Fail(NeedsToken ? "The server did not accept the registration token." : "The server wants more than this app can give to create an account.");
        }
        catch (MatrixException exception)
        {
            return Fail(exception.Errcode switch
            {
                ErrorCodes.UserInUse => "That user ID is taken; choose another.",
                ErrorCodes.Forbidden => "This server does not let apps create accounts.",
                _ => MainViewModel.CleanError(exception.Message),
            });
        }
        catch (HttpRequestException exception)
        {
            return Fail($"The server cannot be reached: {exception.Message}");
        }
    }

    /// <summary>Show <paramref name="message"/> and return false.</summary>
    private bool Fail(string message)
    {
        // Tell, and report failure
        ErrorMessage = message;
        return false;
    }

    /// <summary>Forget the requirements of the previous server.</summary>
    private void ForgetRequirements()
    {
        // Asked again on the next attempt
        requirements = null;
        NeedsToken = false;
        HasPolicies = false;
        AcceptTerms = false;
        Policies.Clear();
    }

    /// <summary>Return whether the form can be sent.</summary>
    private bool CanSignIn() => !IsBusy && UserId.Trim().Length > 0 && Password.Length > 0;
}
