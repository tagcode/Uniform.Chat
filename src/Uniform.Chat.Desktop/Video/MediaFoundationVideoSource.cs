namespace Uniform.Chat.Desktop.Video;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;
using static Uniform.Chat.Desktop.Video.MediaFoundation;

/// <summary>A video decoded by <see href="https://learn.microsoft.com/windows/win32/medfound/source-reader">Media Foundation's source reader</see> on Windows, for <see cref="DecodedVideoPlayback"/>: whatever Windows decodes, such as MP4 with H.264 and AAC, and H.265, VP9 or AV1 where their extensions from the Microsoft Store are installed. One reader gives the pictures as NV12, converted to I420 as they are read, with the file's rotation; another decodes the sound whole, at 48 kHz stereo.</summary>
/// <remarks>Pictures are cropped to their display area, e.g. 1080 of 1088 rows, and those wider than 640 are scaled down while copied, by taking every second, third… pixel, as a message shows them small. Their times count from the first picture, so an MP4 whose edit list Windows does not apply still starts at 0. Seeking reads from the keyframe before the position and drops the pictures before it.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationVideoSource : IVideoSource
{
    /// <summary>The widest picture made for a message.</summary>
    private const int MaxWidth = 640;

    /// <summary>The sample rate and channels the sound is decoded to.</summary>
    private const int SoundRate = 48000, SoundChannels = 2;

    /// <summary>How close before a seek's position a picture still counts as at it, in 100 ns.</summary>
    private const long SeekTolerance = 10_000;

    private readonly int rotation;
    private IMFSourceReader? reader;
    private Layout layout;
    private long origin;
    private (VideoFrame Picture, TimeSpan Time)? pending;
    private bool atStart;
    private bool ended;
    private int failure;

    /// <summary>Open the file: its pictures ready to read from the first, and its sound decoded whole.</summary>
    /// <exception cref="NotSupportedException">When Windows cannot read the file, it has no video, or Windows has no decoder of its video, e.g. H.265 without HEVC Video Extensions, or any on Windows N without its Media Feature Pack.</exception>
    public MediaFoundationVideoSource(string path)
    {
        // The pictures' reader, refusing a file without video before the sound is decoded
        EnterMta();
        if (!IsAvailable) throw new NotSupportedException("Windows has no Media Foundation; Windows N editions need the Media Feature Pack.");
        (reader, rotation) = OpenPictures(path);
        try
        {
            // Length, sound, then the first picture, from whose time the others count
            layout = ReadLayout(reader);
            Duration = ReadDuration(reader);
            Sound = DecodeSound(path);
            if (ReadPicture() is not (IMFSample sample, long time)) throw new NotSupportedException(failure < 0 ? $"Its video does not decode: {Describe(failure)}" : "The video has no pictures.");
            origin = time;
            pending = Convert(sample, time);
            atStart = true;
        }
        catch
        {
            // On a failure, the reader released
            Dispose();
            throw;
        }
    }

    /// <summary>How long the video plays.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The sound, or null.</summary>
    public AudioClip? Sound { get; }

    /// <summary>Whether the last picture was read.</summary>
    public bool IsAtEnd => ended && pending is null;

    /// <summary>Return the next picture, decoded as it is read; null at the end.</summary>
    public (VideoFrame Picture, TimeSpan Time)? Next()
    {
        // The one kept from opening or seeking, else the next decoded
        EnterMta();
        atStart = false;
        if (pending is { } kept)
        {
            pending = null;
            return kept;
        }
        if (ReadPicture() is not (IMFSample sample, long time)) return null;
        // Return it
        return Convert(sample, time);
    }

    /// <summary>Move to <paramref name="position"/>: from the keyframe before it, the pictures before it decoded and dropped, the last of them kept when none comes after.</summary>
    public void SeekTo(TimeSpan position)
    {
        // Not at all to the start when still there
        EnterMta();
        if (reader is null || atStart && position <= TimeSpan.Zero) return;
        atStart = false;
        pending = null;
        PropVariant at = new() { Type = VariantInt64, Value = origin + Math.Max(0, position.Ticks) };
        if (reader.SetCurrentPosition(Guid.Empty, at) < 0)
        {
            ended = true;
            return;
        }
        ended = false;

        // Pictures until the position
        IMFSample? last = null;
        long lastTime = 0;
        while (ReadPicture() is (IMFSample sample, long time))
        {
            Release(last);
            (last, lastTime) = (sample, time);
            if (time - origin + SeekTolerance >= position.Ticks) break;
        }
        // Done
        if (last is not null) pending = Convert(last, lastTime);
    }

    /// <summary>Release the reader.</summary>
    public void Dispose()
    {
        // Once
        EnterMta();
        Release(reader);
        reader = null;
    }

    /// <summary>Return a reader of the first video stream of <paramref name="path"/>, decoding to NV12, and the clockwise turn that shows its pictures upright.</summary>
    /// <exception cref="NotSupportedException">When Windows cannot read the file, it has no video, or Windows has no decoder of it.</exception>
    private static (IMFSourceReader Reader, int Rotation) OpenPictures(string path)
    {
        // A reader converting between YUV formats, e.g. 10-bit pictures to NV12
        Check(MFCreateAttributes(out IMFAttributes attributes, 1));
        Check(attributes.SetUINT32(EnableAdvancedVideoProcessing, 1));
        int created = MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes, out IMFSourceReader opened);
        Release(attributes);
        if (created < 0) throw new NotSupportedException($"Windows cannot read the file: {Describe(created)}");

        // Its first video stream alone, and what it holds
        try
        {
            opened.SetStreamSelection(AllStreams, false);
            if (opened.GetNativeMediaType(FirstVideoStream, 0, out IMFMediaType native) < 0) throw new NotSupportedException("The file has no video.");
            native.GetGUID(Subtype, out Guid codec);
            int turn = native.GetUINT32(VideoRotation, out uint stored) >= 0 ? (int)stored % 360 : 0;
            Release(native);

            // Decoded to NV12
            Check(MFCreateMediaType(out IMFMediaType wanted));
            Check(wanted.SetGUID(MajorType, Video));
            Check(wanted.SetGUID(Subtype, Nv12));
            int set = opened.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, wanted);
            Release(wanted);
            if (set < 0) throw new NotSupportedException(NoDecoder(codec, set));
            Check(opened.SetStreamSelection(FirstVideoStream, true));
            // Return the reader
            return (opened, turn);
        }
        catch
        {
            // On a refusal, the reader released
            Release(opened);
            throw;
        }
    }

    /// <summary>Return why Windows does not decode video of <paramref name="codec"/>, from the failure <paramref name="result"/>, with where to get its decoder.</summary>
    private static string NoDecoder(Guid codec, int result)
    {
        // The codec's four letters, and where its decoder comes from
        string name = new([.. codec.ToByteArray().Take(4).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '?')]);
        string hint = name.ToUpperInvariant() switch
        {
            "H264" or "AVC1" => " Windows N editions need the Media Feature Pack.",
            "HEVC" or "H265" or "HVC1" => " Install HEVC Video Extensions from the Microsoft Store.",
            "VP90" or "VP80" => " Install VP9 Video Extensions or Web Media Extensions from the Microsoft Store.",
            "AV01" => " Install AV1 Video Extension from the Microsoft Store.",
            _ => "",
        };
        // Return the message
        return $"Windows has no decoder of {name.Trim()} video: {Describe(result)}{hint}";
    }

    /// <summary>Return the message of a failed HRESULT, which ends in its number.</summary>
    private static string Describe(int result) => Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}";

    /// <summary>Return the presentation's length; zero when the file does not tell.</summary>
    private static TimeSpan ReadDuration(IMFSourceReader source)
    {
        // An unsigned 64-bit count of 100 ns
        if (source.GetPresentationAttribute(MediaSource, PresentationDuration, out PropVariant value) < 0 || value.Type != VariantUInt64) return TimeSpan.Zero;
        // Return it
        return TimeSpan.FromTicks(value.Value);
    }

    /// <summary>Return the layout of the decoded pictures: their size, stride and display area.</summary>
    private static Layout ReadLayout(IMFSourceReader source)
    {
        // The size, then the stride, which NV12 rounds to no less than the width
        Check(source.GetCurrentMediaType(FirstVideoStream, out IMFMediaType type));
        try
        {
            Check(type.GetUINT64(FrameSize, out ulong size));
            int width = (int)(size >> 32), height = (int)(size & 0xFFFFFFFF);
            int stride = type.GetUINT32(DefaultStride, out uint value) >= 0 && (int)value > 0 ? (int)value : width;

            // The display area within it, e.g. without the rows that round 1080 up to 1088
            (int x, int y, int w, int h) = (0, 0, width, height);
            if (type.GetBlob(MinimumDisplayAperture, out VideoArea area, (uint)Marshal.SizeOf<VideoArea>(), out _) >= 0 && area.Width > 0 && area.Height > 0 && area.X >= 0 && area.Y >= 0 && area.X + area.Width <= width && area.Y + area.Height <= height)
                (x, y, w, h) = (area.X & ~1, area.Y & ~1, area.Width, area.Height);
            // Return the layout
            return new Layout(width, height, stride, x, y, w, h);
        }
        finally
        {
            Release(type);
        }
    }

    /// <summary>Read the next decoded picture and its time in 100 ns, taking a new layout when it changes; null at the end, also on an error, which is kept.</summary>
    private (IMFSample Sample, long Time)? ReadPicture()
    {
        // Samples, past gaps without one
        while (reader is not null && !ended)
        {
            int result = reader.ReadSample(FirstVideoStream, 0, out _, out uint flags, out long time, out IMFSample? sample);
            if (result >= 0 && (flags & MediaTypeChanged) != 0) layout = ReadLayout(reader);
            if (result < 0 || (flags & (EndOfStream | ReadError)) != 0)
            {
                Release(sample);
                failure = result;
                ended = true;
                break;
            }
            if (sample is null) continue;
            return (sample, time);
        }
        // On the end
        return null;
    }

    /// <summary>Return a decoded sample as a picture with its time from the first, and release it; null when its buffer does not lock or is short.</summary>
    private (VideoFrame Picture, TimeSpan Time)? Convert(IMFSample sample, long time)
    {
        // Its buffer
        try
        {
            if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) < 0) return null;
            try
            {
                VideoFrame? picture = Lock(buffer);
                if (picture is null) return null;
                picture.Rotation = rotation;
                // Return it
                return (picture, TimeSpan.FromTicks(Math.Max(0, time - origin)));
            }
            finally
            {
                Release(buffer);
            }
        }
        finally
        {
            Release(sample);
        }
    }

    /// <summary>Lock a buffer, in its own rows where it is a 2-D one, else in rows of the default stride, and return its picture; null when it does not lock or is short.</summary>
    private VideoFrame? Lock(IMFMediaBuffer buffer)
    {
        // Without a copy where it can
        if (buffer is IMF2DBuffer2 surface && surface.Lock2DSize(LockRead, out IntPtr scanline0, out int pitch, out IntPtr start, out uint length) >= 0)
        {
            try { return pitch > 0 ? ToFrame(scanline0, (long)length - (scanline0 - start), pitch, layout) : null; }
            finally { surface.Unlock2D(); }
        }
        if (buffer.Lock(out IntPtr data, out _, out uint current) < 0) return null;
        // Return the picture
        try { return ToFrame(data, current, layout.Stride, layout); }
        finally { buffer.Unlock(); }
    }

    /// <summary>Return an NV12 picture of <paramref name="layout"/>, its rows <paramref name="pitch"/> bytes apart in <paramref name="size"/> bytes, as I420 of its display area, every step-th pixel of one wider than <see cref="MaxWidth"/>; null when the data is short.</summary>
    internal static unsafe VideoFrame? ToFrame(IntPtr data, long size, int pitch, Layout layout)
    {
        // The luma rows, then the rows of interleaved U and V
        int chromaRows = (layout.Height + 1) / 2;
        if ((long)pitch * (layout.Height + chromaRows) > size || pitch < layout.Width) return null;
        byte* luma = (byte*)data, chroma = luma + (long)pitch * layout.Height;

        // Each plane, sampled
        int step = (layout.CropWidth + MaxWidth - 1) / MaxWidth;
        int width = Math.Max(1, layout.CropWidth / step), height = Math.Max(1, layout.CropHeight / step), chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        byte[] y = new byte[width * height], u = new byte[chromaWidth * chromaHeight], v = new byte[chromaWidth * chromaHeight];
        for (int row = 0; row < height; row++)
        {
            byte* source = luma + (long)(layout.CropY + row * step) * pitch + layout.CropX;
            for (int column = 0; column < width; column++) y[row * width + column] = source[column * step];
        }
        for (int row = 0; row < chromaHeight; row++)
        {
            byte* source = chroma + (long)Math.Min(layout.CropY / 2 + row * step, chromaRows - 1) * pitch + layout.CropX;
            for (int column = 0; column < chromaWidth; column++)
            {
                int pair = Math.Min(column * step, (layout.Width - layout.CropX) / 2 - 1) * 2;
                u[row * chromaWidth + column] = source[pair];
                v[row * chromaWidth + column] = source[pair + 1];
            }
        }
        // Return the picture
        return new VideoFrame(width, height, y, u, v);
    }

    /// <summary>Decode the first audio stream whole to 16-bit PCM at 48 kHz stereo, through the resampler; null when there is none or it does not decode, or it is too long to hold.</summary>
    private static AudioClip? DecodeSound(string path)
    {
        // A reader of its first audio stream alone, decoding to PCM
        if (MFCreateSourceReaderFromURL(Path.GetFullPath(path), null, out IMFSourceReader sound) < 0) return null;
        try
        {
            sound.SetStreamSelection(AllStreams, false);
            if (sound.SetStreamSelection(FirstAudioStream, true) < 0) return null;
            Check(MFCreateMediaType(out IMFMediaType wanted));
            wanted.SetGUID(MajorType, Audio);
            wanted.SetGUID(Subtype, Pcm);
            wanted.SetUINT32(AudioBitsPerSample, 16);
            wanted.SetUINT32(AudioSampleRate, SoundRate);
            wanted.SetUINT32(AudioChannels, SoundChannels);
            wanted.SetUINT32(AudioBlockAlignment, 2 * SoundChannels);
            wanted.SetUINT32(AudioBytesPerSecond, 2 * SoundChannels * SoundRate);
            int set = sound.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted);
            Release(wanted);
            if (set < 0) return null;

            // Samples until the end
            List<short> samples = [];
            while (true)
            {
                if (sound.ReadSample(FirstAudioStream, 0, out _, out uint flags, out _, out IMFSample? sample) < 0 || (flags & ReadError) != 0) return null;
                if (sample is not null) Append(samples, sample);
                if ((flags & EndOfStream) != 0) break;
                if (samples.Count > AudioFile.DefaultMaxSamples) return null;
            }
            // Return the clip
            return samples.Count == 0 ? null : new AudioClip([.. samples], SoundRate, SoundChannels);
        }
        finally
        {
            Release(sound);
        }
    }

    /// <summary>Append a sample's 16-bit PCM, and release it.</summary>
    private static unsafe void Append(List<short> samples, IMFSample sample)
    {
        // Its bytes, as they are
        try
        {
            if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) < 0) return;
            if (buffer.Lock(out IntPtr data, out _, out uint length) >= 0)
            {
                samples.AddRange(new ReadOnlySpan<short>((void*)data, (int)length / 2));
                buffer.Unlock();
            }
            Release(buffer);
        }
        finally
        {
            // Done
            Release(sample);
        }
    }

    /// <summary>The layout of decoded NV12 pictures: their size as decoded, the bytes from a row to the next in a 1-D buffer, and the area to show, its corner at even pixels.</summary>
    internal readonly record struct Layout(int Width, int Height, int Stride, int CropX, int CropY, int CropWidth, int CropHeight);
}
