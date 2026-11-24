namespace Uniform.Chat.Avalonia.Services;

using NAudio.Wave;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;

/// <summary>Plays an audio file, decoded in C# by <see cref="AudioFile"/>, on a media speaker with <see cref="AudioClipPlayer"/>.</summary>
/// <param name="openSpeaker">Opens the media speaker.</param>
public sealed class AudioPlayback(Func<IWavePlayer> openSpeaker) : IMediaPlayback
{
    private readonly AudioClipPlayer player = new(openSpeaker);
    private AudioClip? clip;
    private TimeSpan start;

    /// <summary>How long it plays.</summary>
    public TimeSpan Duration => clip?.Duration ?? TimeSpan.Zero;

    /// <summary>The position played; where it starts before the first play.</summary>
    public TimeSpan Position => player.Clip is null ? start : player.Position;

    /// <summary>Whether it plays now.</summary>
    public bool IsPlaying => player.IsPlaying;

    /// <summary>Not raised: audio has no pictures.</summary>
    public event Action<VideoFrame>? PictureReady { add { } remove { } }

    /// <summary>Raised when it has played to its end.</summary>
    public event Action? Ended;

    /// <summary>Raised when the speaker fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Decode the file on the thread pool.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        // Decoded, and the player's events passed on
        clip = await AudioFile.DecodeFileAsync(path, cancellationToken);
        player.Ended += _ => Ended?.Invoke();
        // Done
        player.Failed += e => Failed?.Invoke(e);
    }

    /// <summary>Play from the position.</summary>
    public Task PlayAsync() => clip is null ? Task.CompletedTask : player.Clip is null ? player.PlayAsync(clip, start) : player.ResumeAsync();

    /// <summary>Pause at the position.</summary>
    public Task PauseAsync() => player.PauseAsync();

    /// <summary>Move to <paramref name="position"/>.</summary>
    public Task SeekAsync(TimeSpan position)
    {
        // Before the first play, where it starts
        if (player.Clip is null) start = position;
        else player.Seek(position);
        // Done
        return Task.CompletedTask;
    }

    /// <summary>Stop and close the speaker.</summary>
    public ValueTask DisposeAsync() => player.DisposeAsync();
}
