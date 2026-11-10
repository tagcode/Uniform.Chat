namespace Uniform.Chat.Desktop.Cameras;

using Uniform.Matrix.Calls;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;

/// <summary>A desktop webcam encoded with the pure C# <see cref="Vp8Encoder"/>: a subclass captures I420 pictures on a thread of its own and gives them to <see cref="Deliver"/>, which paces, previews and encodes them. Up to 30 pictures per second normally; at half size and 10 per second when the peer asks for less.</summary>
public abstract class EncodingCamera : IVideoCamera
{
    private const long FallbackBelow = 300_000, RestoreFrom = 600_000;
    private readonly Vp8Encoder encoder = new(40);
    private volatile bool low;
    private volatile bool paused;
    private volatile int quantizer = 40;
    private long lastFrame;
    private int previews;

    /// <inheritdoc />
    public event Action<byte[]>? FrameEncoded;

    /// <inheritdoc />
    public event Action<VideoFrame>? Previewed;

    /// <summary>Whether to capture at half size, as the peer asked for less.</summary>
    protected bool Low => low;

    /// <inheritdoc />
    public abstract bool CanSwitch { get; }

    /// <inheritdoc />
    public abstract void Start();

    /// <inheritdoc />
    public abstract void SwitchCamera();

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>Make the next frame a keyframe.</summary>
    public void RequestKeyframe() => encoder.RequestKeyframe();

    /// <summary>Stop sending and previewing, or start again; the device keeps capturing, so it resumes at once.</summary>
    public void SetPaused(bool pause) => paused = pause;

    /// <summary>Follow the peer's limit: a coarser quantizer for lower rates; half size at 10 per second below <see cref="FallbackBelow"/>, full size from <see cref="RestoreFrom"/>.</summary>
    public void LimitBitrate(long bitsPerSecond)
    {
        // Quantizer by the rate; the size between the thresholds as is
        quantizer = bitsPerSecond >= 1_200_000 ? 35 : bitsPerSecond >= 800_000 ? 40 : bitsPerSecond >= 500_000 ? 48 : 56;
        if (bitsPerSecond < FallbackBelow) low = true;
        else if (bitsPerSecond >= RestoreFrom) low = false;
    }

    /// <summary>Take a captured picture: dropped while paused or ahead of the frame rate; every third one previewed; encoded and given.</summary>
    protected void Deliver(Func<VideoFrame> picture)
    {
        // At the frame rate, unless paused
        TimeSpan interval = TimeSpan.FromSeconds(1.0 / (low ? 10 : 30));
        if (paused || System.Diagnostics.Stopwatch.GetElapsedTime(lastFrame) < interval) return;
        lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();

        // Converted only when kept, then previewed and encoded
        VideoFrame converted = picture();
        if (++previews % 3 == 0) Previewed?.Invoke(converted);
        encoder.Quantizer = quantizer;
        // Done
        FrameEncoded?.Invoke(encoder.Encode(converted));
    }
}
