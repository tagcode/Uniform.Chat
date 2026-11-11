namespace Uniform.Chat.Avalonia.Services;

/// <summary>A file shared to the app, copied where the app may read it.</summary>
/// <param name="Path">The copy.</param>
/// <param name="Name">Its name to send, e.g. <c>photo.jpg</c>.</param>
public sealed record SharedFile(string Path, string Name);

/// <summary>What another app shared to this one, e.g. a link from a browser or photos from a gallery.</summary>
/// <param name="Text">Shared text, such as a link; null for none.</param>
/// <param name="Files">Shared files.</param>
public sealed record SharedContent(string? Text, IReadOnlyList<SharedFile> Files)
{
    /// <summary>A short description, e.g. <c>a link</c>, <c>photo.jpg</c> or <c>3 files</c>.</summary>
    public string Summary => Files.Count switch
    {
        0 => Text is not null && Uri.TryCreate(Text.Trim(), UriKind.Absolute, out _) ? "a link" : "a text",
        1 => Files[0].Name,
        _ => $"{Files.Count} files",
    };
}

/// <summary>Takes what the platform's share sheet hands to the app, e.g. Android's, for the chat to offer: a room is chosen, then the text goes into its message box and the files wait to be sent.</summary>
public static class ShareInbox
{
    private static readonly Lock gate = new();
    private static SharedContent? pending;

    /// <summary>Raised when something is shared; on the platform's thread.</summary>
    public static event Action? Arrived;

    /// <summary>Hand over shared content, replacing what was not taken yet.</summary>
    public static void Put(SharedContent content)
    {
        // Kept until taken
        lock (gate) pending = content;
        // Done
        Arrived?.Invoke();
    }

    /// <summary>Return the shared content and forget it; null when there is none.</summary>
    public static SharedContent? Take()
    {
        // Once
        lock (gate)
        {
            SharedContent? taken = pending;
            pending = null;
            // Return content
            return taken;
        }
    }
}
