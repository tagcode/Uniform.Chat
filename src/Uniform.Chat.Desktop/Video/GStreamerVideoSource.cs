namespace Uniform.Chat.Desktop.Video;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Audio.Decoding;
using Uniform.Matrix.Video;
using static Uniform.Chat.Desktop.Video.GStreamer;

/// <summary>A video decoded by GStreamer on Linux, for <see cref="DecodedVideoPlayback"/>: whatever its installed plugins decode, such as MP4 with H.264 or H.265 and AAC, WebM and Matroska. One pipeline gives the pictures as I420, turned upright by the file's orientation, as they are pulled; another decodes the sound whole, at 48 kHz stereo.</summary>
/// <remarks>Pictures wider than 640 are scaled down while copied, by taking every second, third… pixel, as a message shows them small. Their times are stream times, from the file's start, which the sound and seeking use: an MP4's edit list may start its pictures' own times later, e.g. after B-frames' delay.</remarks>
[SupportedOSPlatform("linux")]
internal sealed class GStreamerVideoSource : IVideoSource
{
    /// <summary>The widest picture made for a message.</summary>
    private const int MaxWidth = 640;

    /// <summary>How long to wait for a picture, in nanoseconds.</summary>
    private const ulong PullTimeout = 100_000_000;

    /// <summary>How long to wait for a pipeline's state, in nanoseconds.</summary>
    private const ulong StateTimeout = 10_000_000_000;

    /// <summary>The picture pipeline: decoded, upright, I420, into a sink pulled from. Common sound codecs stay encoded, on a pad left unlinked; the "(ANY)" caps take decoders' output whatever its memory.</summary>
    private const string PicturePipeline = "filesrc name=source ! decodebin caps=\"video/x-raw(ANY);audio/x-raw(ANY);audio/mpeg;audio/x-opus;audio/x-vorbis;audio/x-ac3;audio/x-eac3\" ! videoconvert ! videoflip video-direction=auto ! videoconvert ! video/x-raw,format=I420 ! appsink name=sink sync=false max-buffers=3";

    /// <summary>The sound pipeline: decoded, 16-bit stereo at 48 kHz, into a sink pulled from. Common video codecs stay encoded, so the pictures are not decoded twice; others are, and left unlinked.</summary>
    private const string SoundPipeline = "filesrc name=source ! decodebin caps=\"audio/x-raw(ANY);video/x-raw(ANY);video/x-h264;video/x-h265;video/x-vp8;video/x-vp9;video/x-av1;video/mpeg\" ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,layout=interleaved,rate=48000,channels=2 ! appsink name=sink sync=false";

    private readonly nint pipeline;
    private readonly nint sink;
    private TimeSpan last;

    /// <summary>Open the file: its sound decoded whole, its pictures ready to pull.</summary>
    /// <exception cref="NotSupportedException">When GStreamer cannot decode its video, e.g. without the plugin of its codec.</exception>
    public GStreamerVideoSource(string path)
    {
        // The sound first, which may not be there
        Sound = DecodeSound(path);

        // The pictures, prerolled; their length
        (pipeline, sink) = Start(PicturePipeline, path, StatePaused);
        if (gst_element_get_state(pipeline, out _, out _, StateTimeout) == StateChangeFailure || gst_app_sink_is_eos(sink))
        {
            string error = TakeError(pipeline) ?? "The file has no video.";
            Dispose();
            throw new NotSupportedException(error);
        }
        Duration = gst_element_query_duration(pipeline, FormatTime, out long nanoseconds) ? TimeSpan.FromTicks(nanoseconds / 100) : TimeSpan.Zero;
        // Done: pulled as fast as wanted
        gst_element_set_state(pipeline, StatePlaying);
    }

    /// <summary>How long the pictures play.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The sound, or null.</summary>
    public AudioClip? Sound { get; }

    /// <summary>Whether the last picture was pulled.</summary>
    public bool IsAtEnd => gst_app_sink_is_eos(sink);

    /// <summary>Return the next picture, or null when none came within a moment.</summary>
    public (VideoFrame Picture, TimeSpan Time)? Next()
    {
        // A sample, its picture and time
        nint sample = gst_app_sink_try_pull_sample(sink, PullTimeout);
        if (sample == 0) return null;
        try
        {
            nint caps = gst_sample_get_caps(sample);
            nint structure = caps == 0 ? 0 : gst_caps_get_structure(caps, 0);
            if (structure == 0 || !gst_structure_get_int(structure, "width", out int width) || !gst_structure_get_int(structure, "height", out int height)) return null;
            nint buffer = gst_sample_get_buffer(sample);
            ulong pts = (ulong)Marshal.ReadInt64(buffer, BufferPtsOffset);
            nint segment = gst_sample_get_segment(sample);
            ulong streamTime = pts == ClockTimeNone || segment == 0 ? pts : gst_segment_to_stream_time(segment, FormatTime, pts);
            if (streamTime != ClockTimeNone) last = TimeSpan.FromTicks((long)(streamTime / 100));
            if (!gst_buffer_map(buffer, out MapInfo map, MapRead)) return null;
            try
            {
                // Return the picture
                return ToFrame(map.Data, (long)map.Size, width, height) is VideoFrame picture ? (picture, last) : null;
            }
            finally
            {
                gst_buffer_unmap(buffer, ref map);
            }
        }
        finally
        {
            gst_mini_object_unref(sample);
        }
    }

    /// <summary>Move to <paramref name="position"/>, from which the decoder gives pictures, those before it decoded but dropped.</summary>
    public void SeekTo(TimeSpan position)
    {
        // Flushed and exact, then settled
        gst_element_seek_simple(pipeline, FormatTime, SeekFlushAccurate, position.Ticks * 100);
        gst_element_get_state(pipeline, out _, out _, StateTimeout);
        // Done
        last = position;
    }

    /// <summary>Stop and release the pipeline.</summary>
    public void Dispose()
    {
        // Stopped, then released
        gst_element_set_state(pipeline, StateNull);
        gst_object_unref(sink);
        gst_object_unref(pipeline);
    }

    /// <summary>Return a pipeline of <paramref name="description"/> reading <paramref name="path"/>, set to <paramref name="state"/>, and its sink.</summary>
    private static (nint Pipeline, nint Sink) Start(string description, string path, int state)
    {
        // Built, the file set apart from the text, so it needs no quoting
        nint created = gst_parse_launch(description, out nint error);
        if (created == 0) throw new NotSupportedException("GStreamer cannot make its pipeline.");
        nint source = gst_bin_get_by_name(created, "source");
        gst_util_set_object_arg(source, "location", path);
        gst_object_unref(source);
        nint found = gst_bin_get_by_name(created, "sink");
        gst_element_set_state(created, state);
        // Return them
        return (created, found);
    }

    /// <summary>Decode the sound whole; null when there is none or it does not decode, or it is too long to hold.</summary>
    private static AudioClip? DecodeSound(string path)
    {
        // Pulled until the end, or an error
        (nint sound, nint soundSink) = Start(SoundPipeline, path, StatePlaying);
        try
        {
            if (gst_element_get_state(sound, out _, out _, StateTimeout) == StateChangeFailure) return null;
            List<short> samples = [];
            while (!gst_app_sink_is_eos(soundSink))
            {
                nint sample = gst_app_sink_try_pull_sample(soundSink, 2 * PullTimeout);
                if (sample == 0)
                {
                    if (TakeError(sound) is not null) return null;
                    continue;
                }
                nint buffer = gst_sample_get_buffer(sample);
                if (gst_buffer_map(buffer, out MapInfo map, MapRead))
                {
                    short[] chunk = new short[(int)map.Size / 2];
                    Marshal.Copy(map.Data, chunk, 0, chunk.Length);
                    samples.AddRange(chunk);
                    gst_buffer_unmap(buffer, ref map);
                }
                gst_mini_object_unref(sample);
                if (samples.Count > AudioFile.DefaultMaxSamples) return null;
            }
            // Return the clip
            return samples.Count == 0 ? null : new AudioClip([.. samples], 48000, 2);
        }
        finally
        {
            gst_element_set_state(sound, StateNull);
            gst_object_unref(soundSink);
            gst_object_unref(sound);
        }
    }

    /// <summary>Return an I420 picture of <paramref name="width"/> by <paramref name="height"/> in GStreamer's default layout as a <see cref="VideoFrame"/>, every step-th pixel of one wider than <see cref="MaxWidth"/>; null when the data is short.</summary>
    private static VideoFrame? ToFrame(nint data, long size, int width, int height)
    {
        // The planes' strides and offsets, rows rounded up to four bytes
        int lumaStride = (width + 3) & ~3, chromaStride = (((width + 1) / 2) + 3) & ~3;
        int evenHeight = (height + 1) & ~1;
        long uOffset = (long)lumaStride * evenHeight, vOffset = uOffset + (long)chromaStride * evenHeight / 2;
        if (vOffset + (long)chromaStride * evenHeight / 2 > size) return null;

        // Each plane, sampled
        int step = (width + MaxWidth - 1) / MaxWidth;
        int outWidth = Math.Max(1, width / step), outHeight = Math.Max(1, height / step), outChromaWidth = (outWidth + 1) / 2, outChromaHeight = (outHeight + 1) / 2;
        byte[] y = Sample(data, lumaStride, outWidth, outHeight, step);
        byte[] u = Sample(data + (nint)uOffset, chromaStride, outChromaWidth, outChromaHeight, step);
        byte[] v = Sample(data + (nint)vOffset, chromaStride, outChromaWidth, outChromaHeight, step);
        // Return the picture
        return new VideoFrame(outWidth, outHeight, y, u, v);
    }

    /// <summary>Copy every <paramref name="step"/>-th pixel of every <paramref name="step"/>-th row of a plane.</summary>
    private static byte[] Sample(nint plane, int stride, int width, int height, int step)
    {
        // Rows straight when not scaled
        byte[] packed = new byte[width * height];
        byte[] row = step == 1 ? [] : new byte[stride];
        for (int line = 0; line < height; line++)
        {
            nint start = plane + (nint)((long)line * step * stride);
            if (step == 1)
            {
                Marshal.Copy(start, packed, line * width, width);
                continue;
            }
            Marshal.Copy(start, row, 0, Math.Min(stride, (width - 1) * step + 1));
            for (int column = 0; column < width; column++) packed[line * width + column] = row[column * step];
        }
        // Return pixels
        return packed;
    }
}
