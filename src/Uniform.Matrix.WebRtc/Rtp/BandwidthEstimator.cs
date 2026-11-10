namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Estimates the bitrate a received stream may have from its loss, report by report, as loss-based congestion control does: down by a part when much is lost, up slowly while little is.</summary>
/// <param name="start">The estimate before any report, in bits per second.</param>
/// <param name="minimum">The lowest estimate.</param>
/// <param name="maximum">The highest estimate.</param>
public sealed class BandwidthEstimator(long start = 1_500_000, long minimum = 150_000, long maximum = 2_500_000)
{
    /// <summary>Loss, in 256ths, above which the estimate goes down: about 10%.</summary>
    private const int HighLoss = 26;
    /// <summary>Loss, in 256ths, below which the estimate goes up: about 2%.</summary>
    private const int LowLoss = 5;

    /// <summary>The estimate, in bits per second.</summary>
    public long Bitrate { get; private set; } = start;

    /// <summary>Take a report interval's loss, <paramref name="fractionLost"/> in 256ths; return the new estimate.</summary>
    public long Update(byte fractionLost)
    {
        // Down to 70% on much loss, up by 8% on little, else as is
        if (fractionLost > HighLoss) Bitrate = Math.Max(minimum, Bitrate * 7 / 10);
        else if (fractionLost < LowLoss) Bitrate = Math.Min(maximum, Bitrate * 108 / 100);
        // Return estimate
        return Bitrate;
    }
}
