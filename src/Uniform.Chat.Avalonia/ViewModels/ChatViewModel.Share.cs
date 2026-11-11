namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;

// Content shared from other apps: the room to share to, then its text in the message box and its files to send.
public sealed partial class ChatViewModel
{
    /// <summary>What was shared and waits for a room to be chosen; null when nothing does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingShare), nameof(PendingShareText))]
    public partial SharedContent? PendingShare { get; set; }

    /// <summary>Whether a room should be chosen for shared content.</summary>
    public bool HasPendingShare => PendingShare is not null;

    /// <summary>What to ask while a room is chosen.</summary>
    public string PendingShareText => PendingShare is null ? "" : $"Choose a room to share {PendingShare.Summary} to.";

    /// <summary>Shared files that wait to be sent in the open room; null when none do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedFiles), nameof(SharedFilesText))]
    public partial IReadOnlyList<SharedFile>? SharedFiles { get; set; }

    /// <summary>Whether shared files wait to be sent.</summary>
    public bool HasSharedFiles => SharedFiles is { Count: > 0 };

    /// <summary>What waits to be sent, e.g. <c>Share photo.jpg</c>.</summary>
    public string SharedFilesText => SharedFiles is not { Count: > 0 } files ? "" : files.Count == 1 ? $"Share {files[0].Name}" : $"Share {files.Count} files";

    /// <summary>Follow the share sheet, and take what was shared before this view existed, e.g. what started the app.</summary>
    private void ListenForShares()
    {
        // Now and later
        ShareInbox.Arrived += OnShared;
        // Done
        OnShared();
    }

    /// <summary>Take what was shared, on the UI thread, and show the rooms to choose from.</summary>
    private void OnShared() => ui.Post(() =>
    {
        // The newest share replaces an earlier one not used yet
        if (ShareInbox.Take() is not SharedContent content) return;
        DeleteSharedFiles(PendingShare?.Files);
        PendingShare = content;
        // Done: the room list, where one pane shows one or the other
        if (IsNarrow) SelectedRoom = null;
    });

    /// <summary>Use the share in the room just chosen: its text into the message box, its files waiting for Send.</summary>
    private void ApplyShare(RoomItemViewModel room)
    {
        // A joined room that takes messages
        if (PendingShare is not SharedContent content || room.IsInvite || room.IsServerNotice) return;
        PendingShare = null;
        if (!string.IsNullOrWhiteSpace(content.Text)) Composer = Composer.Length == 0 ? content.Text.Trim() : $"{Composer}\n{content.Text.Trim()}";
        // Done
        if (content.Files.Count > 0) SharedFiles = content.Files;
    }

    /// <summary>Stop sharing: forget what waits for a room or for Send.</summary>
    [RelayCommand]
    private void CancelShare()
    {
        // The copies too
        DeleteSharedFiles(PendingShare?.Files);
        DeleteSharedFiles(SharedFiles);
        PendingShare = null;
        // Done
        SharedFiles = null;
    }

    /// <summary>Send the shared files in the open room, one by one, then delete the copies.</summary>
    [RelayCommand]
    private async Task SendSharedFilesAsync()
    {
        // Taken, so a second press sends nothing more
        if (SharedFiles is not { Count: > 0 } files) return;
        SharedFiles = null;
        foreach (SharedFile file in files)
        {
            try
            {
                await using (FileStream content = File.OpenRead(file.Path)) await SendFileAsync(content, file.Name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // On a copy that went missing
                Notice = $"Sharing {file.Name} failed: {exception.Message}";
            }
        }
        // Done
        DeleteSharedFiles(files);
    }

    /// <summary>Delete the copies of shared files, quietly when they are gone already.</summary>
    private static void DeleteSharedFiles(IReadOnlyList<SharedFile>? files)
    {
        // Each copy
        foreach (SharedFile file in files ?? [])
        {
            try { File.Delete(file.Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
