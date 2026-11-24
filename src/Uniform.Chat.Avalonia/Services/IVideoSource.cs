namespace Uniform.Chat.Avalonia.Services;

using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;

/// <summary>A video file decoded by a platform, for <see cref="DecodedVideoPlayback"/>: its pictures one after another with their times, from where it was moved to, and its sound decoded whole.</summary>
/// <remarks>Made by opening the file, on the thread pool; its members are called one at a time, from any thread.</remarks>
public interface IVideoSource : IDisposable
{
    /// <summary>How long the video plays.</summary>
    TimeSpan Duration { get; }

    /// <summary>The sound, decoded whole and mixed down to stereo; null for none.</summary>
    AudioClip? Sound { get; }

    /// <summary>Whether the last picture was read.</summary>
    bool IsAtEnd { get; }

    /// <summary>Return the next picture and its time; null when none is ready yet, or at the end.</summary>
    (VideoFrame Picture, TimeSpan Time)? Next();

    /// <summary>Move so that <see cref="Next"/> reads from the keyframe before <paramref name="position"/>.</summary>
    void SeekTo(TimeSpan position);
}
