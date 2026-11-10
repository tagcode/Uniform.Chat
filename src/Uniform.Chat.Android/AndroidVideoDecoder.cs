namespace Uniform.Chat.Android;

using global::Android.Media;
using Uniform.Matrix.Video;

/// <summary>Decodes VP8 with Android's own decoder, <see cref="MediaCodec"/>, often in hardware: far faster than C# on slow phones, and lighter on the battery. Pictures come out as images and are converted to <see cref="VideoFrame"/>s.</summary>
/// <remarks>Used synchronously from one thread: each frame is queued, then the pictures ready are taken; a decoder may hold a frame or two before giving it.</remarks>
public sealed class AndroidVideoDecoder : IVideoDecoder
{
    private const string Vp8 = "video/x-vnd.on2.vp8";
    /// <summary>How long to wait for an input buffer, in microseconds.</summary>
    private const long InputWait = 300_000;
    /// <summary>How long to wait for the first picture after a frame, in microseconds.</summary>
    private const long OutputWait = 5_000;
    private const int TryAgainLater = -1;
    private const int OutputFormatChanged = -2;
    private long frames;
    private long pictures;
    private long refused;
    private long lastReport;
    private readonly MediaCodec codec;
    private readonly MediaCodec.BufferInfo info = new();
    private long presentation;

    /// <summary>Create and start a VP8 decoder giving flexible YUV 4:2:0 images.</summary>
    /// <exception cref="Java.Lang.Exception">When the device has no VP8 decoder.</exception>
    public AndroidVideoDecoder()
    {
        // A size to start from; the stream's own size follows
        codec = MediaCodec.CreateDecoderByType(Vp8);
        MediaFormat format = MediaFormat.CreateVideoFormat(Vp8, 640, 480);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
        codec.Configure(format, null, null, MediaCodecConfigFlags.None);
        // Done
        codec.Start();
    }

    /// <summary>Take out the pictures ready, so the decoder never stalls on full outputs; queue the frame; then take out what it gave. Return the newest picture, or null for none yet.</summary>
    public VideoFrame? Decode(ReadOnlySpan<byte> frame)
    {
        // Pictures ready from earlier frames
        VideoFrame? newest = Drain(0);

        // Into an input buffer, timed a frame apart
        int input = codec.DequeueInputBuffer(InputWait);
        if (input < 0)
        {
            refused++;
            Report();
            throw new InvalidDataException("The video decoder takes no more frames.");
        }
        Java.Nio.ByteBuffer buffer = codec.GetInputBuffer(input)!;
        buffer.Clear();
        buffer.Put(frame.ToArray());
        codec.QueueInputBuffer(input, 0, frame.Length, presentation, MediaCodecBufferFlags.None);
        presentation += 33_333;
        frames++;

        // Done: this frame's picture, when quick
        newest = Drain(OutputWait) ?? newest;
        Report();
        return newest;
    }

    /// <summary>Take out every picture ready, the first after waiting up to <paramref name="wait"/> microseconds; return the newest.</summary>
    private VideoFrame? Drain(long wait)
    {
        // Until the decoder has none
        VideoFrame? newest = null;
        while (true)
        {
            int output = codec.DequeueOutputBuffer(info, newest is null ? wait : 0);
            if (output == TryAgainLater) return newest;
            if (output == OutputFormatChanged)
            {
                global::Android.Util.Log.Info("UniformChat", $"Video decoder {codec.Name} gives {codec.OutputFormat}");
                continue;
            }
            if (output < 0) continue;
            using (global::Android.Media.Image? image = codec.GetOutputImage(output))
            {
                if (image is not null)
                {
                    newest = AndroidImages.ToVideoFrame(image);
                    pictures++;
                    image.Close();
                }
            }
            codec.ReleaseOutputBuffer(output, false);
        }
    }

    /// <summary>Log the counts every five seconds, to see how the decoder keeps up.</summary>
    private void Report()
    {
        // Now and then
        if (System.Diagnostics.Stopwatch.GetElapsedTime(lastReport) < TimeSpan.FromSeconds(5)) return;
        lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
        // Done
        global::Android.Util.Log.Info("UniformChat", $"Video decoder {codec.Name}: {frames} frames in, {pictures} pictures out, {refused} refused");
    }

    /// <summary>Stop and release the decoder.</summary>
    public void Dispose()
    {
        // Stopped, then released
        try { codec.Stop(); }
        catch (Java.Lang.IllegalStateException) { }
        // Done
        codec.Release();
    }
}
