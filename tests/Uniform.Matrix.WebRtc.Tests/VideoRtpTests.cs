namespace Uniform.Matrix.WebRtc.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>VP8 over RTP: frames split into packets and put together again, also out of order; a lost packet waits for a keyframe; and the RTCP a video receiver sends.</summary>
public class VideoRtpTests
{
    /// <summary>A test vector packetized, its packets shuffled within a window as a network might, then put together and decoded, gives the vector's pictures.</summary>
    [Fact]
    public void ReassembleTestVector()
    {
        // Arrange: the frames as RTP packets of at most 300 bytes, reordered in groups of five
        string vector = Path.Combine(AppContext.BaseDirectory, "TestVectors", "vp80-00-comprehensive-017.ivf");
        string[] expected = [.. File.ReadAllLines(vector + ".md5").Where(l => l.Length > 0).Select(l => l.Split(' ')[0])];
        List<RtpPacket> packets = Packetize(ReadIvf(File.ReadAllBytes(vector)), 300);
        Random random = new(7);
        ShuffleWindows(packets, 5, random);

        // Act: depacketize and decode
        Vp8Depacketizer depacketizer = new();
        Vp8Decoder decoder = new();
        List<string> actual = [];
        foreach (RtpPacket packet in packets)
        {
            foreach (Vp8EncodedFrame frame in depacketizer.Push(packet))
            {
                if (decoder.Decode(frame.Data) is VideoFrame picture) actual.Add(Md5(picture));
            }
        }

        // Assert: every picture, and none lost
        Assert.Equal(expected, actual);
        Assert.False(depacketizer.NeedsKeyframe);
    }

    /// <summary>After a lost packet, once the reorder window has passed, inter frames are held back until a keyframe arrives, which <see cref="Vp8Depacketizer.NeedsKeyframe"/> asks for.</summary>
    [Fact]
    public void WaitForKeyframeAfterLoss()
    {
        // Arrange: a keyframe and three inter frames of two packets each, the second inter frame's first packet lost
        byte[] key = [0x00, 1, 2, 3, 4, 5], inter = [0x01, 1, 2, 3, 4, 5];
        List<RtpPacket> packets = Packetize([key, inter, inter, inter, key, inter], 6 + Vp8Packetizer.DescriptorLength - 3);
        packets.RemoveAt(4);
        Vp8Depacketizer depacketizer = new(reorderWindow: 3);

        // Act
        List<Vp8EncodedFrame> given = [.. packets.SelectMany(depacketizer.Push)];

        // Assert: the keyframe and the first inter frame, then nothing until the next keyframe and the frame after it
        Assert.Equal([true, false, true, false], given.Select(f => f.IsKeyframe));
        Assert.Equal([0u, 3000u, 12000u, 15000u], given.Select(f => f.Timestamp));
        Assert.False(depacketizer.NeedsKeyframe);
    }

    /// <summary>A stream that starts with an inter frame waits for a keyframe; late and duplicate packets are ignored.</summary>
    [Fact]
    public void StartAtKeyframe()
    {
        // Arrange
        byte[] key = [0x00, 9], inter = [0x01, 9];
        List<RtpPacket> packets = Packetize([inter, key, inter], 100);
        Vp8Depacketizer depacketizer = new();

        // Act: in order, then the first again
        List<Vp8EncodedFrame> given = [.. packets.SelectMany(depacketizer.Push)];
        given.AddRange(depacketizer.Push(packets[0]));
        given.AddRange(depacketizer.Push(packets[2]));

        // Assert
        Assert.Equal([3000u, 6000u], given.Select(f => f.Timestamp));
    }

    /// <summary>A compound packet has a receiver report, the CNAME and a picture loss indication, each with the right lengths; sender reports are read back.</summary>
    [Fact]
    public void BuildRtcp()
    {
        // Arrange and act
        ReportBlock block = new(0xAABBCCDD, 64, -2, 0x0001_0005, 90, 0x11223344, 65536);
        byte[] compound = Rtcp.Compound(0x01020304, [block], "uniform", pictureLossOf: 0xAABBCCDD);

        // Assert: receiver report of one block
        Assert.Equal(0x81, compound[0]);
        Assert.Equal(Rtcp.ReceiverReportType, compound[1]);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(compound.AsSpan(2)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(compound.AsSpan(8)));
        Assert.Equal([64, 0xFF, 0xFF, 0xFE], compound[12..16]);

        // Assert: CNAME padded to 32 bits, then the PLI
        int sdes = 32;
        Assert.Equal(Rtcp.SourceDescriptionType, compound[sdes + 1]);
        int sdesLength = (BinaryPrimitives.ReadUInt16BigEndian(compound.AsSpan(sdes + 2)) + 1) * 4;
        Assert.Equal("uniform"u8.ToArray(), compound[(sdes + 10)..(sdes + 17)]);
        int pli = sdes + sdesLength;
        Assert.Equal([0x81, Rtcp.PayloadFeedbackType, 0, 2], compound[pli..(pli + 4)]);
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(compound.AsSpan(pli + 8)));
        Assert.Equal(compound.Length, pli + 12);

        // Assert: a sender report's middle NTP bits
        byte[] senderReport = new byte[28];
        senderReport[0] = 0x80;
        senderReport[1] = Rtcp.SenderReportType;
        BinaryPrimitives.WriteUInt16BigEndian(senderReport.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(senderReport.AsSpan(4), 0x55);
        BinaryPrimitives.WriteUInt32BigEndian(senderReport.AsSpan(8), 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(senderReport.AsSpan(12), 0x9ABCDEF0);
        Assert.Equal((0x55u, 0x56789ABCu), Assert.Single(Rtcp.ReadSenderReports([.. senderReport, .. compound])));
    }

    /// <summary>Statistics count the highest sequence number across a wraparound and the packets lost.</summary>
    [Fact]
    public void CountLoss()
    {
        // Arrange: sequence numbers 65533 to 2, with 65535 and 1 lost
        ReceiverStatistics statistics = new(90000);
        foreach (ushort sequence in new ushort[] { 65533, 65534, 0, 2 }) statistics.Received(new RtpPacket { PayloadType = 96, Sequence = sequence, Timestamp = 0, Ssrc = 9, Payload = [] });

        // Act
        ReportBlock report = statistics.Report()!;

        // Assert: six expected, four received
        Assert.Equal(9u, report.Ssrc);
        Assert.Equal(0x0001_0002u, report.HighestSequence);
        Assert.Equal(2, report.CumulativeLost);
        Assert.Equal(256 * 2 / 6, report.FractionLost);
        Assert.Equal(0, statistics.Report()!.FractionLost);
    }

    /// <summary>Return RTP packets of <paramref name="frames"/>, 3000 ticks apart, with payloads of at most <paramref name="maxPayload"/> bytes.</summary>
    private static List<RtpPacket> Packetize(IEnumerable<byte[]> frames, int maxPayload)
    {
        // One packetizer, sequence numbers on, the marker on each frame's last packet
        Vp8Packetizer packetizer = new();
        List<RtpPacket> packets = [];
        ushort sequence = 65530;
        uint timestamp = 0;
        foreach (byte[] frame in frames)
        {
            IReadOnlyList<byte[]> payloads = packetizer.Packetize(frame, maxPayload);
            for (int i = 0; i < payloads.Count; i++) packets.Add(new RtpPacket { PayloadType = 96, Marker = i == payloads.Count - 1, Sequence = sequence++, Timestamp = timestamp, Ssrc = 1, Payload = payloads[i] });
            timestamp += 3000;
        }
        // Return packets
        return packets;
    }

    /// <summary>Shuffle <paramref name="packets"/> in place within each run of <paramref name="size"/>.</summary>
    private static void ShuffleWindows(List<RtpPacket> packets, int size, Random random)
    {
        // Window by window
        for (int at = 0; at + size <= packets.Count; at += size)
        {
            RtpPacket[] window = [.. packets.GetRange(at, size)];
            random.Shuffle(window);
            for (int i = 0; i < size; i++) packets[at + i] = window[i];
        }
    }

    /// <summary>Return the frames of an IVF file.</summary>
    private static IEnumerable<byte[]> ReadIvf(byte[] file)
    {
        // After the header, size-prefixed frames
        int at = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        while (at + 12 <= file.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
            yield return file[(at + 12)..(at + 12 + size)];
            at += 12 + size;
        }
    }

    /// <summary>Return the MD5 of a picture's planes.</summary>
    private static string Md5(VideoFrame picture)
    {
        // Y, U, V
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(picture.Y);
        md5.AppendData(picture.U);
        md5.AppendData(picture.V);
        // Return hex
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
