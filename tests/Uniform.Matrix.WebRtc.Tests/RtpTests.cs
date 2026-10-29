namespace Uniform.Matrix.WebRtc.Tests;

using Uniform.Matrix.WebRtc.Rtp;

/// <summary>Tests of <see cref="RtpPacket"/> and <see cref="JitterBuffer"/>.</summary>
public class RtpTests
{
    /// <summary>Encode a packet and parse it back; skip CSRCs, extensions and padding when parsing.</summary>
    [Fact]
    public void RoundTrip()
    {
        // Plain
        RtpPacket packet = new() { PayloadType = 111, Marker = true, Sequence = 0xFFFF, Timestamp = 0x12345678, Ssrc = 0xCAFEBABE, Payload = [1, 2, 3] };
        Assert.Equal(packet with { }, RtpPacket.Parse(packet.Encode())! with { Payload = packet.Payload });
        Assert.Equal(packet.Payload, RtpPacket.Parse(packet.Encode())!.Payload);

        // One CSRC, a one-word extension, and two bytes of padding
        byte[] complex = Convert.FromHexString("B16F0001000000020000000300000004BEDE00010A0B0C0D0102030002");
        RtpPacket parsed = RtpPacket.Parse(complex)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Payload);
        // Done
        Assert.Equal(111, parsed.PayloadType);
    }

    /// <summary>Tell RTCP from RTP when multiplexed.</summary>
    [Fact]
    public void TellRtcp()
    {
        // Sender report 200, and an RTP packet of type 111
        Assert.True(RtpPacket.IsRtcp([0x80, 200]));
        Assert.False(RtpPacket.IsRtcp([0x80, 111]));
        // Done
        Assert.False(RtpPacket.IsRtcp([0x80, 0x80 | 111]));
    }

    /// <summary>Fill, then play in order across the sequence's wrap; skip a lost packet once later ones pile up, and drop it when it comes after.</summary>
    [Fact]
    public void PlayInOrder()
    {
        // Three held before playing, arriving out of order
        JitterBuffer buffer = new(3);
        buffer.Add(Packet(0xFFFE));
        Assert.Null(buffer.Next(out bool playing));
        Assert.False(playing);
        buffer.Add(Packet(0x0000));
        buffer.Add(Packet(0xFFFF));

        // In order across the wrap
        Assert.Equal(0xFFFE, buffer.Next(out playing)!.Sequence);
        Assert.True(playing);
        Assert.Equal(0xFFFF, buffer.Next(out _)!.Sequence);
        Assert.Equal(0x0000, buffer.Next(out _)!.Sequence);

        // 1 missing: waited for while few are behind it, lost when three are
        buffer.Add(Packet(2));
        Assert.Null(buffer.Next(out playing));
        Assert.True(playing);
        Assert.Equal(0, buffer.Lost);
        buffer.Add(Packet(3));
        buffer.Add(Packet(4));
        Assert.Null(buffer.Next(out _));
        Assert.Equal(1, buffer.Lost);

        // 1 is late now
        buffer.Add(Packet(1));
        // Done
        Assert.Equal(2, buffer.Next(out _)!.Sequence);
    }

    /// <summary>Wait for a packet that is late when the buffer runs dry, and play it when it comes, rather than skipping it.</summary>
    [Fact]
    public void WaitForLatePacket()
    {
        // Playing, then dry
        JitterBuffer buffer = new(1);
        buffer.Add(Packet(10));
        Assert.Equal(10, buffer.Next(out _)!.Sequence);
        Assert.Null(buffer.Next(out bool playing));
        Assert.True(playing);

        // The late one plays
        buffer.Add(Packet(11));
        Assert.Equal(11, buffer.Next(out _)!.Sequence);
        // Done
        Assert.Equal(0, buffer.Lost);
        Assert.Equal(1, buffer.Underruns);
    }

    /// <summary>Return a packet with a sequence number.</summary>
    private static RtpPacket Packet(ushort sequence) => new() { PayloadType = 111, Sequence = sequence, Timestamp = sequence * 960u, Ssrc = 1, Payload = [] };
}
