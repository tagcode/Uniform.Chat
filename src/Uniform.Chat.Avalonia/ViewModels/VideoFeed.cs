namespace Uniform.Chat.Avalonia.ViewModels;

using Uniform.Matrix.Video;

/// <summary>The newest picture of a video, for a <see cref="Views.VideoView"/> to show; pictures may come on any thread.</summary>
public sealed class VideoFeed
{
    private VideoFrame? latest;

    /// <summary>The newest picture, or null for none.</summary>
    public VideoFrame? Latest => Volatile.Read(ref latest);

    /// <summary>Raised when <see cref="Latest"/> changes, on the thread that changed it.</summary>
    public event Action? Updated;

    /// <summary>Make <paramref name="picture"/> the newest, replacing any not yet shown.</summary>
    public void Push(VideoFrame picture)
    {
        // Newest wins
        Volatile.Write(ref latest, picture);
        // Done
        Updated?.Invoke();
    }

    /// <summary>Show no picture.</summary>
    public void Clear()
    {
        // None
        Volatile.Write(ref latest, null);
        // Done
        Updated?.Invoke();
    }
}
