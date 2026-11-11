namespace Uniform.Matrix.Calls.Tests;

/// <summary>Tests of <see cref="CallService.RingExpiry"/>.</summary>
public sealed class RingExpiryTests
{
    /// <summary>Ring from the sender's time when its clock agrees with the server's, else from the server's: a clock two hours behind, as one in the wrong time zone, still rings; at most two minutes.</summary>
    [Fact]
    public void ExpireRings()
    {
        // The server's time of a ring
        long origin = DateTimeOffset.Parse("2026-11-11T11:59:32Z").ToUnixTimeMilliseconds();

        // Done
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(origin - 5_000 + 60_000), CallService.RingExpiry(origin - 5_000, origin, 60_000));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(origin + 60_000), CallService.RingExpiry(origin - 7_200_000, origin, 60_000));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(origin + 60_000), CallService.RingExpiry(origin + 7_200_000, origin, 60_000));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(origin + 120_000), CallService.RingExpiry(origin, origin, 3_600_000));
    }
}
