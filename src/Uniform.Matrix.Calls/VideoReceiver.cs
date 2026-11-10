namespace Uniform.Matrix.Calls;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>Decodes the video a peer connection receives, on its own task, and gives the pictures in <see cref="PictureDecoded"/>.</summary>
/// <remarks>A device that cannot keep up, by its decode times or by falling behind, asks the sender once for less with a REMB. Frames queue as they arrive. When decoding falls behind by <see cref="MaxQueued"/> frames, or a frame does not decode, the frames up to the next keyframe are skipped and the peer is asked for one, as inter frames only decode after the frames before them.</remarks>
public sealed class VideoReceiver : IAsyncDisposable
{
    /// <summary>How many frames may wait for the decoder before they are given up.</summary>
    private const int MaxQueued = 30;
    private readonly AudioPeerConnection connection;
    private readonly ILogger logger;
    private readonly Channel<Vp8EncodedFrame> queue = Channel.CreateUnbounded<Vp8EncodedFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Vp8Decoder decoder = new();
    private readonly Task decoding;
    private int queued;
    private long decoded;
    private long skipped;
    private long decodeTicks;
    private double averageMilliseconds;
    private int askedLower;

    /// <summary>Decode time, as a moving average, past which this device cannot keep up and asks for less, in milliseconds: about 14 pictures per second.</summary>
    private const double SlowDecode = 70;

    /// <summary>The bitrate asked of a sender that this device cannot keep up with, which senders of this app meet with 320x240 at 10 pictures per second.</summary>
    public const long FallbackBitrate = 200_000;
    private bool waitKeyframe;

    /// <summary>Start decoding the video of <paramref name="connection"/>.</summary>
    /// <param name="connection">A connection that receives video.</param>
    /// <param name="logger">Logger of decoding failures.</param>
    public VideoReceiver(AudioPeerConnection connection, ILogger logger)
    {
        // Frames in, a task decoding them
        this.connection = connection;
        this.logger = logger;
        connection.VideoReceived += OnFrame;
        // Done
        decoding = Task.Run(DecodeAsync);
    }

    /// <summary>Raised with each decoded picture, on the decoding task; a handler should be quick, e.g. keep the newest picture to show.</summary>
    public event Action<VideoFrame>? PictureDecoded;

    /// <summary>Pictures decoded so far.</summary>
    public long Decoded => Interlocked.Read(ref decoded);

    /// <summary>Average time to decode a frame so far, e.g. to see whether a device keeps up.</summary>
    public TimeSpan AverageDecodeTime => Decoded == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref decodeTicks) / Decoded);

    /// <summary>Frames skipped so far, because decoding fell behind or a frame did not decode.</summary>
    public long Skipped => Interlocked.Read(ref skipped);

    /// <summary>Stop decoding.</summary>
    public async ValueTask DisposeAsync()
    {
        // No more frames, then the decoder finishes
        connection.VideoReceived -= OnFrame;
        queue.Writer.TryComplete();
        // Done
        await decoding.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Queue a received frame.</summary>
    private void OnFrame(Vp8EncodedFrame frame)
    {
        // Counted, to see when decoding falls behind
        if (queue.Writer.TryWrite(frame)) Interlocked.Increment(ref queued);
    }

    /// <summary>Decode the queued frames until stopped.</summary>
    private async Task DecodeAsync()
    {
        // Frame by frame
        await foreach (Vp8EncodedFrame frame in queue.Reader.ReadAllAsync())
        {
            int behind = Interlocked.Decrement(ref queued);
            if (behind >= MaxQueued && !waitKeyframe) Skip("decoding fell behind");
            if (waitKeyframe && !frame.IsKeyframe)
            {
                Interlocked.Increment(ref skipped);
                continue;
            }
            waitKeyframe = false;
            Decode(frame);
        }
    }

    /// <summary>Decode a frame and give its picture; on a bad frame, wait for a keyframe.</summary>
    private void Decode(Vp8EncodedFrame frame)
    {
        // Decoded, or skipped from here
        VideoFrame? picture;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { picture = decoder.Decode(frame.Data); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogDebug(e, "Video frame {Timestamp} did not decode", frame.Timestamp);
            Skip("a frame did not decode");
            return;
        }
        if (picture is null) return;
        TimeSpan took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref decodeTicks, took.Ticks);
        long count = Interlocked.Increment(ref decoded);
        averageMilliseconds = count == 1 ? took.TotalMilliseconds : averageMilliseconds * 0.9 + took.TotalMilliseconds * 0.1;
        if (count >= 15 && averageMilliseconds > SlowDecode) AskLower($"decoding takes {averageMilliseconds:F0} ms");
        // Done
        PictureDecoded?.Invoke(picture);
    }

    /// <summary>Ask the sender, once, for <see cref="FallbackBitrate"/>, as this device cannot keep up.</summary>
    private void AskLower(string why)
    {
        // Once per call
        if (Interlocked.Exchange(ref askedLower, 1) == 1) return;
        logger.LogInformation("Asking for less video: {Why}", why);
        // Done
        _ = connection.RequestBitrateAsync(FallbackBitrate);
    }

    /// <summary>Skip frames up to the next keyframe, and ask the peer for one; when decoding fell behind, also for less video.</summary>
    private void Skip(string why)
    {
        // Less, when this device is too slow
        if (why == "decoding fell behind") AskLower(why);
        // Ask once per skip
        logger.LogDebug("Waiting for a video keyframe: {Why}", why);
        waitKeyframe = true;
        Interlocked.Increment(ref skipped);
        // Done
        _ = connection.RequestKeyframeAsync();
    }
}
