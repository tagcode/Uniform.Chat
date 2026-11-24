namespace Uniform.Chat.Avalonia.Services;

using System.Diagnostics;
using NAudio.Wave;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;

/// <summary>Plays a video whose pictures and sound a platform decodes, through an <see cref="IVideoSource"/>: Android's MediaCodec, or GStreamer on Linux. Its sound plays on the media speaker and keeps the time; each picture shows when its time comes.</summary>
/// <remarks>Seeking moves the source to the keyframe before the position and reads to the first picture at it. A video without sound keeps its own time.</remarks>
/// <param name="open">Opens the source of a file, on the thread pool.</param>
/// <param name="openSpeaker">Opens the media speaker.</param>
public sealed class DecodedVideoPlayback(Func<string, IVideoSource> open, Func<IWavePlayer> openSpeaker) : IMediaPlayback
{
    /// <summary>The longest wait between checks of the time.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Stopwatch watch = new();
    private IVideoSource? source;
    private AudioClipPlayer? speaker;
    private TimeSpan offset;
    private (VideoFrame Picture, TimeSpan Time)? pending;
    private CancellationTokenSource? running;
    private Task? loop;

    /// <summary>How long it plays.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>The position: the sound's, or the clock's without sound.</summary>
    public TimeSpan Position
    {
        get
        {
            // From the speaker while it has the clip, else the clock
            TimeSpan position = speaker?.Clip is not null ? speaker.Position : offset + watch.Elapsed;
            // Return it, within the length
            return position < Duration ? position : Duration;
        }
    }

    /// <summary>Whether it plays now.</summary>
    public bool IsPlaying => loop is not null;

    /// <summary>Raised with each picture to show.</summary>
    public event Action<VideoFrame>? PictureReady;

    /// <summary>Raised when it has played to its end.</summary>
    public event Action? Ended;

    /// <summary>Raised when the speaker fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Open the source and show the first picture, on the thread pool.</summary>
    public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        // The source, its length with its sound's
        source = open(path);
        Duration = source.Sound is AudioClip sound && sound.Duration > source.Duration ? sound.Duration : source.Duration;
        // Done: the first picture
        ShowAt(TimeSpan.Zero);
    }, cancellationToken);

    /// <summary>Play from the position; from the start when it had ended.</summary>
    public async Task PlayAsync()
    {
        // Not twice
        await gate.WaitAsync();
        try
        {
            if (loop is not null || source is null) return;
            if (Position >= Duration || source.IsAtEnd && pending is null) await Task.Run(() => MoveTo(TimeSpan.Zero));
            if (source.Sound is AudioClip sound)
            {
                speaker ??= CreateSpeaker();
                if (speaker.Clip is null) await speaker.PlayAsync(sound, Position);
                else await speaker.ResumeAsync();
            }
            else
            {
                watch.Start();
            }
            running = new CancellationTokenSource();
            loop = Task.Run(() => RunAsync(running.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Pause at the position.</summary>
    public async Task PauseAsync()
    {
        // The pictures, then the sound or clock
        await gate.WaitAsync();
        try { await StopLoopAsync(); }
        finally { gate.Release(); }
    }

    /// <summary>Move to <paramref name="position"/>, showing the picture there.</summary>
    public async Task SeekAsync(TimeSpan position)
    {
        // Paused while moving, then on as before
        await gate.WaitAsync();
        try
        {
            bool playing = loop is not null;
            await StopLoopAsync();
            TimeSpan target = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
            await Task.Run(() => MoveTo(target));
            if (!playing) return;
            if (speaker?.Clip is not null) await speaker.ResumeAsync();
            else watch.Start();
            running = new CancellationTokenSource();
            loop = Task.Run(() => RunAsync(running.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stop, and release the source and the speaker.</summary>
    public async ValueTask DisposeAsync()
    {
        // The pictures, the sound, then the source
        await gate.WaitAsync();
        try
        {
            await StopLoopAsync();
            if (speaker is not null) await speaker.DisposeAsync();
            speaker = null;
            source?.Dispose();
            source = null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Make the speaker, whose end ends the video.</summary>
    private AudioClipPlayer CreateSpeaker()
    {
        // The sound's end, and its failures
        AudioClipPlayer player = new(openSpeaker);
        player.Ended += ended => _ = FinishAsync();
        player.Failed += exception => Failed?.Invoke(exception);
        // Return it
        return player;
    }

    /// <summary>Set the clock and the sound to <paramref name="position"/>, and show the picture there.</summary>
    private void MoveTo(TimeSpan position)
    {
        // Clock and sound, then the picture
        watch.Reset();
        offset = position;
        speaker?.Seek(position);
        // Done
        ShowAt(position);
    }

    /// <summary>Read from the keyframe before <paramref name="position"/> to the first picture at or after it, and show it.</summary>
    private void ShowAt(TimeSpan position)
    {
        // From the keyframe
        if (source is null) return;
        source.SeekTo(position);
        pending = null;

        // Pictures until the position, the last one before it kept in case none comes after
        VideoFrame? shown = null;
        for (int attempts = 0; attempts < 2000 && !source.IsAtEnd; attempts++)
        {
            if (source.Next() is not (VideoFrame picture, TimeSpan time)) continue;
            shown = picture;
            if (time + TimeSpan.FromMilliseconds(1) >= position) break;
        }
        // Done
        if (shown is not null) PictureReady?.Invoke(shown);
    }

    /// <summary>Show pictures as they are due, until stopped or the last one; without sound, the end ends the video.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Each picture at its time
        try
        {
            while (!cancellationToken.IsCancellationRequested && source is IVideoSource video)
            {
                pending ??= video.IsAtEnd ? null : video.Next();
                if (pending is not (VideoFrame picture, TimeSpan time))
                {
                    if (video.IsAtEnd && video.Sound is null && Position >= Duration)
                    {
                        _ = FinishAsync();
                        return;
                    }
                    if (video.IsAtEnd) await Task.Delay(Tick, cancellationToken);
                    continue;
                }
                TimeSpan now = Position;
                if (time > now)
                {
                    TimeSpan wait = time - now;
                    await Task.Delay(wait < Tick ? wait : Tick, cancellationToken);
                    continue;
                }
                pending = null;
                PictureReady?.Invoke(picture);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
    }

    /// <summary>Stop at the end, and tell.</summary>
    private async Task FinishAsync()
    {
        // Stopped at the end
        await gate.WaitAsync();
        try
        {
            await StopLoopAsync();
            watch.Reset();
            offset = Duration;
        }
        finally
        {
            gate.Release();
        }
        // Done
        Ended?.Invoke();
    }

    /// <summary>Stop the pictures, the sound and the clock, keeping the position.</summary>
    private async Task StopLoopAsync()
    {
        // The loop, which returns right after starting the end, so it is not waited on by itself
        if (running is not null) await running.CancelAsync();
        if (loop is not null) await loop.ContinueWith(_ => { }, TaskScheduler.Default);
        loop = null;
        running = null;
        if (speaker?.Clip is not null) await speaker.PauseAsync();
        // Done: the clock keeps where it is
        offset += watch.Elapsed;
        watch.Reset();
    }
}
