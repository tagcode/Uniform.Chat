namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Protocol.Core;

/// <summary>The app's current page: the sign-in form, resuming, or the chat screen; it owns the running connection.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly GuiPlatform platform;
    private readonly IUiDispatcher ui;
    private readonly AccountMemory memory;
    private ClientConnection? connection;
    private ChatViewModel? chat;
    private IDisposable? keptConnected;

    /// <summary>Create the app state for <paramref name="platform"/>.</summary>
    public MainViewModel(GuiPlatform platform, IUiDispatcher ui)
    {
        // Sign-in form first
        this.platform = platform;
        this.ui = ui;
        memory = new AccountMemory(platform.ApplicationName);
        Settings = new SettingsViewModel(platform.ApplicationName, canNotify: platform.Notifier is not null, platform);
        Login = new LoginViewModel(SignInAsync, new AccountCreator(platform.HttpHandler?.Invoke()));
        Page = Login;
    }

    /// <summary>Appearance and notification settings, applied by the app.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The sign-in form, kept between attempts.</summary>
    public LoginViewModel Login { get; }

    /// <summary>The page shown.</summary>
    [ObservableProperty]
    public partial ViewModelBase Page { get; set; }

    /// <summary>Resume the remembered account, or stay on the sign-in form.</summary>
    public async Task StartAsync()
    {
        // Remembered account; no password, as the stored session is resumed
        if (memory.Load() is not SignIn remembered) return;
        Login.UserId = remembered.UserId;
        Login.Homeserver = remembered.Homeserver ?? "";
        Page = new ConnectingViewModel(remembered.UserId);
        string? error = await ConnectAsync(remembered);
        if (error is null) return;

        // The form asks for the password, or says what went wrong
        Login.ErrorMessage = error == PasswordNeeded ? "" : error;
        Page = Login;
    }

    /// <summary>Stop the engine, e.g. when the app closes.</summary>
    public async Task StopAsync()
    {
        // Chat first, then the engine it follows
        keptConnected?.Dispose();
        keptConnected = null;
        if (chat is not null) await chat.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
        chat = null;
        connection = null;
    }

    /// <summary>Sign in from the form; on failure the form shows why.</summary>
    private async Task SignInAsync(SignIn signIn)
    {
        // Connect, remembering the account when it works
        string? error = await ConnectAsync(signIn);
        if (error is not null)
        {
            Login.ErrorMessage = error;
            return;
        }
        memory.Save(signIn);
        Login.Password = "";
    }

    /// <summary>Start the engine and wait for the login; return null and show the chat, or return why it failed.</summary>
    private async Task<string?> ConnectAsync(SignIn signIn)
    {
        // A new engine for this account
        await StopAsync();
        ClientConnection started;
        try
        {
            started = await ClientConnection.StartAsync(platform, signIn);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.Message;
        }

        // Login, or the reason it failed
        try
        {
            await started.WaitForLoginAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await started.DisposeAsync();
            return exception.Message.Contains(nameof(PasswordRequiredException)) || exception.Message.StartsWith("Sign in to ", StringComparison.Ordinal) ? PasswordNeeded : CleanError(exception.Message);
        }

        // Chat over the running engine
        connection = started;
        CallViewModel call = new(started.Calls, ui, platform, started.DisplayNameOf);
        chat = new ChatViewModel(started, ui, platform.SinglePane, SignOutAsync, platform.Notifier, Settings, call);
        chat.Start();
        keptConnected ??= platform.KeepConnected?.Invoke();
        Page = chat;
        return null;
    }

    /// <summary>Log out this device, forget the account and show the sign-in form.</summary>
    private async Task SignOutAsync()
    {
        // Log out on the server, then stop
        ClientConnection? ending = connection;
        if (ending is not null) await ending.LogOutAsync();
        await StopAsync();
        memory.Forget();
        Login.Password = "";
        Login.ErrorMessage = "";
        Page = Login;
    }

    /// <summary>Marker for a failed resume that needs the password.</summary>
    private const string PasswordNeeded = "password";

    /// <summary>Return an engine error without its Matrix error code, e.g. <c>Invalid password</c> for <c>M_FORBIDDEN: Invalid password</c>.</summary>
    internal static string CleanError(string message)
    {
        // M_CODE: text
        int colon = message.IndexOf(": ", StringComparison.Ordinal);
        // Return text
        return message.StartsWith("M_", StringComparison.Ordinal) && colon > 0 ? message[(colon + 2)..] : message;
    }
}
