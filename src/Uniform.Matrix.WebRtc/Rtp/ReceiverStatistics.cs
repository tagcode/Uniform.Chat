namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Counts how one RTP stream arrives, for its reception report, as <see href="https://www.rfc-editor.org/rfc/rfc3550#appendix-A.3">RFC 3550 appendix A</see> does: the highest sequence number with wraparounds, packets lost, interarrival jitter, and the last sender report.</summary>
/// <param name="clockRate">The stream's RTP clock, e.g. 90000 for video.</param>
/// <param name="time">Clock of arrivals.</param>
public sealed class ReceiverStatistics(int clockRate, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly long start = (time ?? TimeProvider.System).GetTimestamp();
    private bool started;
    private ushort maxSequence;
    private uint cycles;
    private uint baseSequence;
    private uint received;
    private uint expectedPrior;
    private uint receivedPrior;
    private double jitter;
    private long lastTransit;
    private uint lastSenderReport;
    private long lastSenderReportArrival;

    /// <summary>The stream, from its first packet.</summary>
    public uint Ssrc { get; private set; }

    /// <summary>Count a received packet.</summary>
    public void Received(RtpPacket packet)
    {
        // The first packet sets the base
        long transit = (long)(clock.GetElapsedTime(start).TotalSeconds * clockRate) - packet.Timestamp;
        if (!started)
        {
            started = true;
            Ssrc = packet.Ssrc;
            maxSequence = packet.Sequence;
            baseSequence = packet.Sequence;
            lastTransit = transit;
            received = 1;
            return;
        }

        // Sequence forwards, counting wraparounds; jitter by the change in transit time
        ushort delta = (ushort)(packet.Sequence - maxSequence);
        if (delta is > 0 and < 0x8000)
        {
            if (packet.Sequence < maxSequence) cycles += 0x10000;
            maxSequence = packet.Sequence;
        }
        received++;
        long d = Math.Abs(transit - lastTransit);
        lastTransit = transit;
        // Done
        jitter += (d - jitter) / 16;
    }

    /// <summary>Note a sender report of the stream, by the middle 32 bits of its NTP time.</summary>
    public void SenderReport(uint ntpMiddle)
    {
        // Echoed in the next report, with the delay since
        lastSenderReport = ntpMiddle;
        lastSenderReportArrival = clock.GetTimestamp();
    }

    /// <summary>Return the report block for now, and start the next interval; null before the first packet.</summary>
    public ReportBlock? Report()
    {
        // Expected from the sequence numbers, lost as the difference
        if (!started) return null;
        uint extended = cycles + maxSequence;
        uint expected = extended - baseSequence + 1;
        int lost = (int)(expected - received);
        uint expectedInterval = expected - expectedPrior, receivedInterval = received - receivedPrior;
        expectedPrior = expected;
        receivedPrior = received;
        int lostInterval = (int)(expectedInterval - receivedInterval);
        byte fraction = expectedInterval == 0 || lostInterval <= 0 ? (byte)0 : (byte)Math.Min(255, (lostInterval << 8) / (int)expectedInterval);

        // The sender report's echo and its delay in 1/65536 seconds
        uint delay = lastSenderReport == 0 ? 0 : (uint)(clock.GetElapsedTime(lastSenderReportArrival).TotalSeconds * 65536);
        // Return block
        return new ReportBlock(Ssrc, fraction, lost, extended, (uint)jitter, lastSenderReport, delay);
    }
}
