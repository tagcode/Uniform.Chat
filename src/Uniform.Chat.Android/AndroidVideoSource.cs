namespace Uniform.Chat.Android;

using global::Android.Media;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;

/// <summary>A video decoded by Android, for <see cref="DecodedVideoPlayback"/>: whatever the phone decodes, such as MP4 with H.264 or H.265 and AAC, or WebM, mostly in hardware. <see cref="MediaExtractor"/> reads the file; <see cref="MediaCodec"/> decodes its pictures into images the CPU reads, converted small for the message, and its sound whole when opened.</summary>
/// <remarks>Pictures come out of the decoder as YUV 4:2:0 images through <see cref="MediaCodec.GetOutputImage"/>, as in calls: a surface such as <see cref="ImageReader"/> may get them in a layout only the GPU reads, on which Android aborts the app when the CPU asks for their planes. The file's rotation, e.g. of a video recorded upright, goes with each picture.</remarks>
internal sealed class AndroidVideoSource : IVideoSource
{
    /// <summary>The widest picture made for a message; wider videos are scaled down while copied.</summary>
    private const int MaxWidth = 640;

    /// <summary>How long to wait for a decoder's buffer, in microseconds.</summary>
    private const long WaitMicroseconds = 10_000;

    private readonly MediaExtractor extractor = new();
    private readonly MediaCodec codec;
    private readonly int rotation;
    private bool inputDone;
    private bool outputDone;

    /// <summary>Open the file: its tracks, its sound decoded whole, and the picture decoder started.</summary>
    /// <exception cref="NotSupportedException">When it has no video, or the phone does not decode it.</exception>
    public AndroidVideoSource(string path)
    {
        // The tracks
        try { extractor.SetDataSource(path); }
        catch (Java.IO.IOException exception) { throw new NotSupportedException($"Android cannot read it: {exception.Message}", exception); }
        int video = -1, audio = -1;
        for (int i = 0; i < extractor.TrackCount; i++)
        {
            string mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime) ?? "";
            if (video < 0 && mime.StartsWith("video/", StringComparison.Ordinal)) video = i;
            else if (audio < 0 && mime.StartsWith("audio/", StringComparison.Ordinal)) audio = i;
        }
        if (video < 0) throw new NotSupportedException("The file has no video.");

        // Length and rotation of the pictures; the sound, if any decodes
        MediaFormat format = extractor.GetTrackFormat(video);
        Duration = format.ContainsKey(MediaFormat.KeyDuration) ? TimeSpan.FromTicks(format.GetLong(MediaFormat.KeyDuration) * 10) : TimeSpan.Zero;
        rotation = format.ContainsKey(MediaFormat.KeyRotation) ? format.GetInteger(MediaFormat.KeyRotation) : ReadRotation(path);
        if (audio >= 0) Sound = DecodeSound(path, audio);

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
        // Done
        extractor.SelectTrack(video);
    }

    /// <summary>How long the pictures play.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The sound, or null.</summary>
    public AudioClip? Sound { get; }

    /// <summary>Whether the decoder gave its last picture.</summary>
    public bool IsAtEnd => outputDone;

    /// <summary>Return the next picture, or null when none is ready yet.</summary>
    public (VideoFrame Picture, TimeSpan Time)? Next() => Step();

    /// <summary>Move the reader to the keyframe before <paramref name="position"/>, the decoder emptied.</summary>
    public void SeekTo(TimeSpan position)
    {
        // From the keyframe
        extractor.SeekTo((long)(position.TotalMilliseconds * 1000), MediaExtractorSeekTo.PreviousSync);
        codec.Flush();
        // Done
        inputDone = outputDone = false;
    }

    /// <summary>Release the decoder and the reader.</summary>
    public void Dispose()
    {
        // Decoder, then reader
        codec.Stop();
        codec.Release();
        extractor.Release();
    }

    /// <summary>Feed the decoder a frame if it has room, and take a picture if it has one: the picture and its time, or null.</summary>
    private (VideoFrame, TimeSpan)? Step()
    {
        // A frame in, or the end of the stream
        if (!inputDone)
        {
            int input = codec.DequeueInputBuffer(WaitMicroseconds);
            if (input >= 0)
            {
                Java.Nio.ByteBuffer buffer = codec.GetInputBuffer(input)!;
                int size = extractor.ReadSampleData(buffer, 0);
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
        int output = codec.DequeueOutputBuffer(info, WaitMicroseconds);
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
