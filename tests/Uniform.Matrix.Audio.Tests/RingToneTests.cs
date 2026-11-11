namespace Uniform.Matrix.Audio.Tests;

/// <summary>Tests of <see cref="RingTone"/>.</summary>
public sealed class RingToneTests
{
    /// <summary>Ring in two bursts with a pause after, every three seconds, below full scale; without a speaker, ring silently.</summary>
    [Fact]
    public void RingInBursts()
    {
        // Loudest sample of a stretch of the cycle, in seconds
        static double Peak(double from, double to) => Enumerable.Range((int)(from * VoiceFormat.SampleRate), (int)((to - from) * VoiceFormat.SampleRate)).Max(i => Math.Abs(RingTone.Sample(i)));

        // On, off, on, off, and the next cycle on
        Assert.InRange(Peak(0, 0.4), 0.1, 0.31);
        Assert.Equal(0, Peak(0.4, 0.6));
        Assert.InRange(Peak(0.6, 1.0), 0.1, 0.31);
        Assert.Equal(0, Peak(1.0, 3.0));
        Assert.InRange(Peak(3.0, 3.4), 0.1, 0.31);
        // Done
        RingTone.Start(null).Dispose();
    }
}
