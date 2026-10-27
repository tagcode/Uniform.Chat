namespace Durum.Matrix.Client.Terminal.Ui;

using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Gui.Terminal;
using global::Terminal.Gui.App;

/// <summary>Asks for the password in a modal dialog on the UI thread.</summary>
public sealed class TuiCredentialPrompt : ICredentialPrompt
{
    private readonly IApplication app;

    /// <summary>Create a prompt shown in <paramref name="app"/>.</summary>
    public TuiCredentialPrompt(IApplication app)
    {
        // Store application
        this.app = app;
    }

    /// <summary>Show the password dialog; throw <see cref="OperationCanceledException"/> when cancelled.</summary>
    public Task<string> GetPasswordAsync(string userId, CancellationToken cancellationToken)
    {
        // Show on the UI thread, completing from there
        TaskCompletionSource<string> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            string? password = Dialogs.Ask(app, "Log in", $"Password for {userId}:", secret: true);
            if (password is null) result.TrySetCanceled(cancellationToken);
            else result.TrySetResult(password);
        });
        // Return pending password
        return result.Task;
    }
}
