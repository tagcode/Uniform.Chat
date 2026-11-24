namespace Uniform.Chat.Avalonia.Services;

using System.Diagnostics;
using NAudio.Wave;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;
using Uniform.Matrix.Video.Webm;

/// <summary>Plays a WebM video in C#: VP8 pictures with <see cref="Vp8Decoder"/>, and Opus sound with <see cref="AudioFile.DecodeOpus"/> on a media speaker, which keeps the time; a video without sound keeps its own.</summary>
/// <remarks>
/// The whole file is read and its sound decoded when opened; pictures are decoded as they are due, on the thread pool. When decoding falls more than half a second behind, it jumps to the latest keyframe due. Seeking decodes from the keyframe before the position.
/// Other codecs, such as VP9 or AV1, are not supported, and Vorbis sound plays as silence.
/// </remarks>
/// <param name="openSpeaker">Opens the media speaker.</param>
public sealed class WebmPlayback(Func<IWavePlayer> openSpeaker) : IMediaPlayback
{
    /// <summary>The largest file read, as it is held whole.</summary>
    private const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>How far behind decoding may fall before it jumps to a keyframe.</summary>
    private static readonly TimeSpan CatchUp = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait between checks of the time.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(15);

    private readonly Vp8Decoder decoder = new();
    private readonly Stopwatch watch = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebmFrame[] pictures = [];
    private AudioClip? sound;
    private AudioClipPlayer? speaker;
    private TimeSpan offset;
    private int next;
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

    /// <summary>Read the file and decode its sound on the thread pool, and show the first picture.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        // The file, read whole
        if (new FileInfo(path).Length > MaxFileBytes) throw new NotSupportedException("The video is too large to play here.");
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken);
        await Task.Run(() =>
        {
            // VP8 pictures, Opus sound where there is
            WebmFile file = WebmFile.Parse(data);
            WebmTrack video = file.Tracks.FirstOrDefault(t => t.Kind == WebmTrackKind.Video) ?? throw new NotSupportedException("The file has no video.");
            if (video.CodecId != "V_VP8") throw new NotSupportedException($"{video.CodecId[2..]} video is not supported here, only VP8.");
            pictures = [.. file.Frames.Where(f => f.Track == video.Number)];
            if (pictures.Length == 0) throw new InvalidDataException("The video has no pictures.");
            if (file.Tracks.FirstOrDefault(t => t.Kind == WebmTrackKind.Audio && t.CodecId == "A_OPUS" && t.CodecPrivate is not null) is WebmTrack audio)
            {
                try { sound = AudioFile.DecodeOpus(audio.CodecPrivate!, file.Frames.Where(f => f.Track == audio.Number).Select(f => f.Data)); }
                catch (Exception exception) when (exception is NotSupportedException or InvalidDataException) { sound = null; }
            }
            Duration = TimeSpan.FromTicks(Math.Max(file.Duration.Ticks, sound?.Duration.Ticks ?? 0));
            // Done: the first picture
            ShowAt(TimeSpan.Zero);
        }, cancellationToken);
    }

    /// <summary>Play from the position; from the start when it had ended.</summary>
    public async Task PlayAsync()
    {
        // Not twice
        await gate.WaitAsync();
        try
        {
            if (loop is not null) return;
            if (Position >= Duration) MoveTo(TimeSpan.Zero);
            TimeSpan from = Position;
            if (sound is not null)
            {
                speaker ??= CreateSpeaker();
                if (speaker.Clip is null) await speaker.PlayAsync(sound, from);
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
            MoveTo(position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position);
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

    /// <summary>Stop, and close the speaker.</summary>
    public async ValueTask DisposeAsync()
    {
        // The pictures, then the sound
        await gate.WaitAsync();
        try
        {
            await StopLoopAsync();
            if (speaker is not null) await speaker.DisposeAsync();
            speaker = null;
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

    /// <summary>Decode from the keyframe before <paramref name="position"/> to the last picture due there, and show it.</summary>
    private void ShowAt(TimeSpan position)
    {
        // The pictures due, from their keyframe
        int last = Math.Max(0, LastDue(position));
        int key = last;
        while (key > 0 && !pictures[key].IsKeyframe) key--;
        VideoFrame? shown = null;
        for (int i = key; i <= last; i++) shown = Decode(i) ?? shown;
        next = last + 1;
        // Done
        if (shown is not null) PictureReady?.Invoke(shown);
    }

    /// <summary>Show pictures as they are due, until stopped or the last one; without sound, the end ends the video.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Each picture at its time
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan now = Position;
                if (next >= pictures.Length)
                {
                    if (sound is null && now >= Duration)
                    {
                        _ = FinishAsync();
                        return;
                    }
                    await Task.Delay(Tick, cancellationToken);
                    continue;
                }
                TimeSpan due = pictures[next].Timestamp;
                if (due > now)
                {
                    TimeSpan wait = due - now;
                    await Task.Delay(wait < Tick ? wait : Tick, cancellationToken);
                    continue;
                }

                // Far behind: on from the latest keyframe due
                if (now - due > CatchUp)
                {
                    int key = LastDue(now);
                    while (key > next && !pictures[key].IsKeyframe) key--;
                    if (key > next) next = key;
                }

                // Decode, and show it unless the next one is due too
                VideoFrame? picture = Decode(next++);
                bool nextDue = next < pictures.Length && pictures[next].Timestamp <= now;
                if (picture is not null && !nextDue) PictureReady?.Invoke(picture);
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

    /// <summary>Return the index of the last picture due at <paramref name="position"/>; -1 when none is.</summary>
    private int LastDue(TimeSpan position)
    {
        // By time, in order
        int index = -1;
        while (index + 1 < pictures.Length && pictures[index + 1].Timestamp <= position) index++;
        // Return it
        return index;
    }

    /// <summary>Decode the picture at <paramref name="index"/>; null for one not shown or broken.</summary>
    private VideoFrame? Decode(int index)
    {
        // Broken frames are skipped
        try { return decoder.Decode(pictures[index].Data.Span); }
        catch (InvalidDataException) { return null; }
    }
}
