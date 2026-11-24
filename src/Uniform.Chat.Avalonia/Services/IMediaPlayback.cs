namespace Uniform.Chat.Avalonia.Services;

using Uniform.Matrix.Video;

/// <summary>Plays one media file of a message, audio or video: open, play, pause, seek, with its position, and the pictures of a video.</summary>
/// <remarks>Made per file: <see cref="AudioPlayback"/> for audio, <see cref="WebmPlayback"/> for WebM video in C#, or a platform's, as <see cref="GuiPlatform.VideoPlayback"/> gives, e.g. Android's MediaPlayer.</remarks>
public interface IMediaPlayback : IAsyncDisposable
{
    /// <summary>How long it plays; known once opened.</summary>
    TimeSpan Duration { get; }

    /// <summary>The position played.</summary>
    TimeSpan Position { get; }

    /// <summary>Whether it plays now.</summary>
    bool IsPlaying { get; }

    /// <summary>Raised with each picture of a video to show, the first one once opened; on any thread.</summary>
    event Action<VideoFrame>? PictureReady;

    /// <summary>Raised when it has played to its end; on any thread.</summary>
    event Action? Ended;

    /// <summary>Raised when playing fails, e.g. on a speaker that does not open; on any thread.</summary>
    event Action<Exception>? Failed;

    /// <summary>Open the file at <paramref name="path"/>, ready to play from the start.</summary>
    /// <exception cref="NotSupportedException">When its format or codec is not one to play here.</exception>
    /// <exception cref="InvalidDataException">When it is broken.</exception>
    Task OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Play from the position; from the start when it had ended.</summary>
    Task PlayAsync();

    /// <summary>Pause at the position.</summary>
    Task PauseAsync();

    /// <summary>Move to <paramref name="position"/>, playing or paused.</summary>
    Task SeekAsync(TimeSpan position);
}
