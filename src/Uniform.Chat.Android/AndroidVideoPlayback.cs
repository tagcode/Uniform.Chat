namespace Uniform.Chat.Android;

using System.Diagnostics;
using global::Android.Media;
using NAudio.Wave;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;

/// <summary>Plays a video message with Android's own decoders: whatever the phone decodes, such as MP4 with H.264 or H.265 and AAC, or WebM, mostly in hardware. <see cref="MediaExtractor"/> reads the file, <see cref="MediaCodec"/> decodes its pictures into images the CPU reads, converted small for the message, and its sound, decoded when opened, plays on the media speaker and keeps the time.</summary>
/// <remarks>
/// Pictures come out of the decoder as YUV 4:2:0 images through <see cref="MediaCodec.GetOutputImage"/>, as in calls: a surface such as <see cref="ImageReader"/> may get them in a layout only the GPU reads, on which Android aborts the app when the CPU asks for their planes.
/// Each picture shows when its time comes; seeking decodes from the keyframe before the position. The file's rotation, e.g. of a video recorded upright, goes with each picture. A video without sound, or whose sound does not decode, keeps its own time.
/// </remarks>
/// <param name="openSpeaker">Opens the media speaker.</param>
internal sealed class AndroidVideoPlayback(Func<IWavePlayer> openSpeaker) : IMediaPlayback
{
    /// <summary>The widest picture made for a message; wider videos are scaled down while copied.</summary>
    private const int MaxWidth = 640;

    /// <summary>How long to wait for a decoder's buffer, in microseconds.</summary>
    private const long WaitMicroseconds = 10_000;

    /// <summary>The longest wait between checks of the time.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Stopwatch watch = new();
    private MediaExtractor? extractor;
    private MediaCodec? codec;
    private AudioClip? sound;
    private AudioClipPlayer? speaker;
    private TimeSpan offset;
    private int rotation;
    private bool inputDone;
    private bool outputDone;
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

    /// <summary>Find the tracks, decode the sound, start the picture decoder, and show the first picture, on the thread pool.</summary>
    public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        // The tracks
        MediaExtractor opened = new();
        extractor = opened;
        try { opened.SetDataSource(path); }
        catch (Java.IO.IOException exception) { throw new NotSupportedException($"Android cannot read it: {exception.Message}", exception); }
        int video = -1, audio = -1;
        for (int i = 0; i < opened.TrackCount; i++)
        {
            string mime = opened.GetTrackFormat(i).GetString(MediaFormat.KeyMime) ?? "";
            if (video < 0 && mime.StartsWith("video/", StringComparison.Ordinal)) video = i;
            else if (audio < 0 && mime.StartsWith("audio/", StringComparison.Ordinal)) audio = i;
        }
        if (video < 0) throw new NotSupportedException("The file has no video.");

        // Length and rotation of the pictures; the sound, if any decodes
        MediaFormat format = opened.GetTrackFormat(video);
        Duration = format.ContainsKey(MediaFormat.KeyDuration) ? TimeSpan.FromTicks(format.GetLong(MediaFormat.KeyDuration) * 10) : TimeSpan.Zero;
        rotation = format.ContainsKey(MediaFormat.KeyRotation) ? format.GetInteger(MediaFormat.KeyRotation) : ReadRotation(path);
        if (audio >= 0) sound = DecodeSound(path, audio);
        if (sound is not null && sound.Duration > Duration) Duration = sound.Duration;

        // The picture decoder, giving YUV 4:2:0 images to read
        string videoMime = format.GetString(MediaFormat.KeyMime)!;
        try
        {
            codec = MediaCodec.CreateDecoderByType(videoMime);
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
            codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            codec.Start();
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or ArgumentException)
        {
            throw new NotSupportedException($"This phone does not decode {videoMime}: {exception.Message}", exception);
        }
        opened.SelectTrack(video);
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
            if (loop is not null || codec is null) return;
            if (Position >= Duration || outputDone && pending is null) await Task.Run(() => MoveTo(TimeSpan.Zero));
            if (sound is not null)
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

    /// <summary>Stop, and release the decoder, the reader and the speaker.</summary>
    public async ValueTask DisposeAsync()
    {
        // The pictures, the sound, then the decoder
        await gate.WaitAsync();
        try
        {
            await StopLoopAsync();
            if (speaker is not null) await speaker.DisposeAsync();
            speaker = null;
            codec?.Stop();
            codec?.Release();
            codec = null;
            extractor?.Release();
            extractor = null;
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

    /// <summary>Decode from the keyframe before <paramref name="position"/> to the first picture at or after it, and show it.</summary>
    private void ShowAt(TimeSpan position)
    {
        // From the keyframe, the decoder emptied
        if (codec is null || extractor is null) return;
        extractor.SeekTo((long)(position.TotalMilliseconds * 1000), MediaExtractorSeekTo.PreviousSync);
        codec.Flush();
        inputDone = outputDone = false;
        pending = null;

        // Pictures until the position, the last one before it kept in case none comes after
        VideoFrame? shown = null;
        for (int attempts = 0; attempts < 2000 && !outputDone; attempts++)
        {
            if (Step() is not (VideoFrame picture, TimeSpan time)) continue;
            shown = picture;
            if (time + TimeSpan.FromMilliseconds(1) < position) continue;
            break;
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
            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= outputDone ? null : Step();
                if (pending is not (VideoFrame picture, TimeSpan time))
                {
                    if (outputDone && sound is null && Position >= Duration)
                    {
                        _ = FinishAsync();
                        return;
                    }
                    if (outputDone) await Task.Delay(Tick, cancellationToken);
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

    /// <summary>Feed the decoder a frame if it has room, and take a picture if it has one: the picture and its time, or null.</summary>
    private (VideoFrame, TimeSpan)? Step()
    {
        // A frame in, or the end of the stream
        if (!inputDone)
        {
            int input = codec!.DequeueInputBuffer(WaitMicroseconds);
            if (input >= 0)
            {
                Java.Nio.ByteBuffer buffer = codec.GetInputBuffer(input)!;
                int size = extractor!.ReadSampleData(buffer, 0);
                if (size < 0)
                {
                    codec.QueueInputBuffer(input, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                    inputDone = true;
                }
                else
                {
                    codec.QueueInputBuffer(input, 0, size, extractor.SampleTime, MediaCodecBufferFlags.None);
                    extractor.Advance();
                }
            }
        }

        // A picture out, copied small, its buffer given back
        MediaCodec.BufferInfo info = new();
        int output = codec!.DequeueOutputBuffer(info, WaitMicroseconds);
        if (output < 0) return null;
        if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) outputDone = true;
        VideoFrame? picture = null;
        if (info.Size > 0)
        {
            using Image? image = codec.GetOutputImage(output);
            if (image is not null)
            {
                picture = AndroidImages.ToVideoFrame(image, MaxWidth);
                picture.Rotation = rotation;
                image.Close();
            }
        }
        codec.ReleaseOutputBuffer(output, false);
        // Return it with its time
        return picture is null ? null : (picture, TimeSpan.FromTicks(info.PresentationTimeUs * 10));
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

    /// <summary>Return the rotation the file's metadata tells, 0 when none.</summary>
    private static int ReadRotation(string path)
    {
        // From the container
        using MediaMetadataRetriever metadata = new();
        metadata.SetDataSource(path);
        // Return degrees
        return int.TryParse(metadata.ExtractMetadata(MetadataKey.VideoRotation), out int degrees) ? degrees : 0;
    }

    /// <summary>Decode the sound of <paramref name="track"/> whole, mixed down to stereo; null when it does not decode or is too long to hold.</summary>
    private static AudioClip? DecodeSound(string path, int track)
    {
        // Its own reader and decoder
        using MediaExtractor reader = new();
        reader.SetDataSource(path);
        reader.SelectTrack(track);
        MediaFormat format = reader.GetTrackFormat(track);
        MediaCodec decoder;
        try
        {
            decoder = MediaCodec.CreateDecoderByType(format.GetString(MediaFormat.KeyMime)!);
            decoder.Configure(format, null, null, MediaCodecConfigFlags.None);
            decoder.Start();
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or ArgumentException)
        {
            return null;
        }

        // Samples until the end, 16-bit, the format as the decoder gives it
        try
        {
            int rate = format.GetInteger(MediaFormat.KeySampleRate), channels = format.GetInteger(MediaFormat.KeyChannelCount);
            List<short> samples = [];
            bool done = false, ended = false;
            MediaCodec.BufferInfo info = new();
            while (!ended)
            {
                if (!done)
                {
                    int input = decoder.DequeueInputBuffer(WaitMicroseconds);
                    if (input >= 0)
                    {
                        int size = reader.ReadSampleData(decoder.GetInputBuffer(input)!, 0);
                        if (size < 0)
                        {
                            decoder.QueueInputBuffer(input, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            done = true;
                        }
                        else
                        {
                            decoder.QueueInputBuffer(input, 0, size, reader.SampleTime, MediaCodecBufferFlags.None);
                            reader.Advance();
                        }
                    }
                }
                int output = decoder.DequeueOutputBuffer(info, WaitMicroseconds);
                if (output == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    MediaFormat changed = decoder.OutputFormat;
                    rate = changed.GetInteger(MediaFormat.KeySampleRate);
                    channels = changed.GetInteger(MediaFormat.KeyChannelCount);
                    continue;
                }
                if (output < 0) continue;
                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) ended = true;
                if (info.Size > 0)
                {
                    Java.Nio.ByteBuffer buffer = decoder.GetOutputBuffer(output)!;
                    byte[] bytes = new byte[info.Size];
                    buffer.Position(info.Offset);
                    buffer.Get(bytes, 0, info.Size);
                    AppendMixed(samples, bytes, channels);
                    if (samples.Count > AudioFile.DefaultMaxSamples) return null;
                }
                decoder.ReleaseOutputBuffer(output, false);
            }
            // Return the clip
            return samples.Count == 0 ? null : new AudioClip([.. samples], rate, Math.Min(channels, 2));
        }
        catch (Java.Lang.Exception)
        {
            // On sound that does not decode: none
            return null;
        }
        finally
        {
            decoder.Stop();
            decoder.Release();
        }
    }

    /// <summary>Append 16-bit little-endian samples of <paramref name="channels"/> channels, more than two mixed down to stereo: even channels left, odd right.</summary>
    private static void AppendMixed(List<short> samples, byte[] bytes, int channels)
    {
        // As they are, or mixed per frame
        int count = bytes.Length / 2;
        if (channels <= 2)
        {
            for (int i = 0; i < count; i++) samples.Add((short)(bytes[2 * i] | bytes[2 * i + 1] << 8));
            return;
        }
        for (int frame = 0; frame + channels <= count; frame += channels)
        {
            int left = 0, right = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                short value = (short)(bytes[2 * (frame + channel)] | bytes[2 * (frame + channel) + 1] << 8);
                if (channel % 2 == 0) left += value;
                else right += value;
            }
            samples.Add((short)(left / ((channels + 1) / 2)));
            samples.Add((short)(right / (channels / 2)));
        }
    }
}
