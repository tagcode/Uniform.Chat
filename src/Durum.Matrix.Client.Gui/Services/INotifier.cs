namespace Durum.Matrix.Client.Gui.Services;

/// <summary>System notifications of new messages, by platform; the app shows them while its window is in the background.</summary>
public interface INotifier
{
    /// <summary>Show a notification with <paramref name="title"/> and <paramref name="body"/>.</summary>
    /// <param name="title">Who wrote, and where.</param>
    /// <param name="body">The message, shortened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ShowAsync(string title, string body, CancellationToken cancellationToken = default);
}
