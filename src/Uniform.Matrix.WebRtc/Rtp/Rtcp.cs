namespace Uniform.Matrix.WebRtc.Rtp;

using System.Buffers.Binary;
using System.Text;

/// <summary>A reception report block of <see href="https://www.rfc-editor.org/rfc/rfc3550#section-6.4.1">RFC 3550 6.4.1</see>: how a stream arrives.</summary>
/// <param name="Ssrc">The stream reported on.</param>
/// <param name="FractionLost">Packets lost since the last report, in 256ths.</param>
/// <param name="CumulativeLost">Packets lost in all, which duplicates may make negative.</param>
/// <param name="HighestSequence">The highest sequence number received, with the count of its wraparounds in the high 16 bits.</param>
/// <param name="Jitter">Interarrival jitter, in the stream's clock.</param>
/// <param name="LastSenderReport">The middle 32 bits of the NTP time of the last sender report received, or 0.</param>
/// <param name="DelaySinceLastSenderReport">Time since that report, in 1/65536 seconds, or 0.</param>
public sealed record ReportBlock(uint Ssrc, byte FractionLost, int CumulativeLost, uint HighestSequence, uint Jitter, uint LastSenderReport, uint DelaySinceLastSenderReport);

/// <summary>Builds and reads the RTCP packets a video receiver needs: a compound of a receiver report, <see href="https://www.rfc-editor.org/rfc/rfc3550#section-6.4.2">RFC 3550 6.4.2</see>, and the sender's CNAME, with a picture loss indication, <see href="https://www.rfc-editor.org/rfc/rfc4585#section-6.3.1">RFC 4585 6.3.1</see>, when a keyframe is needed; and the time of sender reports, for the reports' round-trip fields.</summary>
public static class Rtcp
{
    /// <summary>Packet type of a sender report.</summary>
    public const byte SenderReportType = 200;
    /// <summary>Packet type of a receiver report.</summary>
    public const byte ReceiverReportType = 201;
    /// <summary>Packet type of source descriptions.</summary>
    public const byte SourceDescriptionType = 202;
    /// <summary>Packet type of transport feedback, such as a generic NACK.</summary>
    public const byte TransportFeedbackType = 205;
    /// <summary>Packet type of payload-specific feedback, such as a picture loss indication.</summary>
    public const byte PayloadFeedbackType = 206;

    /// <summary>Return a compound packet: a receiver report from <paramref name="senderSsrc"/> with <paramref name="blocks"/>, its <paramref name="cname"/>, and with <paramref name="pictureLossOf"/> a picture loss indication for that stream.</summary>
    public static byte[] Compound(uint senderSsrc, IReadOnlyList<ReportBlock> blocks, string cname, uint? pictureLossOf = null)
    {
        // Report, source description, then feedback
        List<byte> packet = [.. ReceiverReport(senderSsrc, blocks), .. SourceDescription(senderSsrc, cname)];
        if (pictureLossOf is uint media) packet.AddRange(PictureLossIndication(senderSsrc, media));
        // Return bytes
        return [.. packet];
    }

    /// <summary>Return a sender report, <see href="https://www.rfc-editor.org/rfc/rfc3550#section-6.4.1">RFC 3550 6.4.1</see>, of <paramref name="senderSsrc"/>: the wall-clock time <paramref name="ntp"/> as 64-bit NTP, the stream's RTP time at that moment, the packets and payload bytes sent, and up to 31 report <paramref name="blocks"/> on streams received.</summary>
    public static byte[] SenderReport(uint senderSsrc, ulong ntp, uint rtpTimestamp, uint packets, uint octets, IReadOnlyList<ReportBlock> blocks)
    {
        // A receiver report's layout with the sender information after the SSRC
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blocks.Count, 31);
        byte[] receiver = ReceiverReport(senderSsrc, blocks);
        byte[] packet = new byte[receiver.Length + 20];
        Header(packet, blocks.Count, SenderReportType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), packets);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), octets);
        receiver.AsSpan(8).CopyTo(packet.AsSpan(28));
        // Return bytes
        return packet;
    }

    /// <summary>Return <paramref name="time"/> as 64-bit NTP: seconds since 1900 in the high half, the fraction in the low.</summary>
    public static ulong Ntp(DateTimeOffset time)
    {
        // Ticks since the NTP epoch
        long ticks = (time - new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks;
        ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        ulong fraction = (ulong)((ticks % TimeSpan.TicksPerSecond) * (1L << 32) / TimeSpan.TicksPerSecond);
        // Return both halves
        return seconds << 32 | fraction;
    }

    /// <summary>Return a receiver report from <paramref name="senderSsrc"/> with up to 31 <paramref name="blocks"/>.</summary>
    public static byte[] ReceiverReport(uint senderSsrc, IReadOnlyList<ReportBlock> blocks)
    {
        // Header with the block count, the sender, then 24 bytes per block
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blocks.Count, 31);
        byte[] packet = new byte[8 + 24 * blocks.Count];
        Header(packet, blocks.Count, ReceiverReportType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);
        for (int i = 0; i < blocks.Count; i++)
        {
            Span<byte> block = packet.AsSpan(8 + 24 * i, 24);
            ReportBlock report = blocks[i];
            BinaryPrimitives.WriteUInt32BigEndian(block, report.Ssrc);
            int lost = Math.Clamp(report.CumulativeLost, -0x800000, 0x7FFFFF);
            BinaryPrimitives.WriteUInt32BigEndian(block[4..], (uint)report.FractionLost << 24 | (uint)lost & 0xFFFFFF);
            BinaryPrimitives.WriteUInt32BigEndian(block[8..], report.HighestSequence);
            BinaryPrimitives.WriteUInt32BigEndian(block[12..], report.Jitter);
            BinaryPrimitives.WriteUInt32BigEndian(block[16..], report.LastSenderReport);
            BinaryPrimitives.WriteUInt32BigEndian(block[20..], report.DelaySinceLastSenderReport);
        }
        // Return bytes
        return packet;
    }

    /// <summary>Return a source description with the CNAME of <paramref name="ssrc"/>.</summary>
    public static byte[] SourceDescription(uint ssrc, string cname)
    {
        // One chunk: SSRC, CNAME item, end of items, padded to 32 bits
        byte[] name = Encoding.UTF8.GetBytes(cname);
        if (name.Length > 255) name = name[..255];
        int chunk = 4 + 2 + name.Length + 1;
        chunk = (chunk + 3) & ~3;
        byte[] packet = new byte[4 + chunk];
        Header(packet, 1, SourceDescriptionType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        packet[8] = 1;
        packet[9] = (byte)name.Length;
        name.CopyTo(packet, 10);
        // Return bytes
        return packet;
    }

    /// <summary>Return a picture loss indication from <paramref name="senderSsrc"/>: the stream <paramref name="mediaSsrc"/> lost pictures and should send a keyframe.</summary>
    public static byte[] PictureLossIndication(uint senderSsrc, uint mediaSsrc)
    {
        // Feedback message type 1, no feedback control information
        byte[] packet = new byte[12];
        Header(packet, 1, PayloadFeedbackType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), mediaSsrc);
        // Return bytes
        return packet;
    }

    /// <summary>Return the sender reports in a compound packet: each sender's SSRC and the middle 32 bits of its NTP time, which a receiver report echoes.</summary>
    public static IReadOnlyList<(uint Ssrc, uint NtpMiddle)> ReadSenderReports(ReadOnlySpan<byte> compound)
    {
        // Packet by packet, by their lengths
        List<(uint, uint)> reports = [];
        int at = 0;
        while (at + 4 <= compound.Length && compound[at] >> 6 == 2)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(compound[(at + 2)..]) + 1) * 4;
            if (at + length > compound.Length) break;
            if (compound[at + 1] == SenderReportType && length >= 28)
            {
                uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 4)..]);
                uint high = BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 8)..]), low = BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 12)..]);
                reports.Add((ssrc, high << 16 | low >> 16));
            }
            at += length;
        }
        // Return them
        return reports;
    }

    /// <summary>Return a generic NACK, <see href="https://www.rfc-editor.org/rfc/rfc4585#section-6.2.1">RFC 4585 6.2.1</see>, from <paramref name="senderSsrc"/>: the packets <paramref name="lost"/> of <paramref name="mediaSsrc"/> should be sent again. Each entry names a packet and a mask of the 16 after it.</summary>
    public static byte[] Nack(uint senderSsrc, uint mediaSsrc, IEnumerable<ushort> lost)
    {
        // Entries of a packet ID and its bitmask, in order
        List<(ushort Id, ushort Mask)> entries = [];
        foreach (ushort sequence in lost.Distinct().Order(Comparer<ushort>.Create((a, b) => (short)(ushort)(a - b))))
        {
            if (entries.Count > 0)
            {
                (ushort id, ushort mask) = entries[^1];
                int after = (ushort)(sequence - id);
                if (after is >= 1 and <= 16)
                {
                    entries[^1] = (id, (ushort)(mask | 1 << (after - 1)));
                    continue;
                }
            }
            entries.Add((sequence, 0));
        }

        // Header, the two streams, the entries
        byte[] packet = new byte[12 + 4 * entries.Count];
        Header(packet, 1, TransportFeedbackType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), mediaSsrc);
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + 4 * i), entries[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14 + 4 * i), entries[i].Mask);
        }
        // Return bytes
        return packet;
    }

    /// <summary>Return the sequence numbers that generic NACKs in a compound packet ask of <paramref name="mediaSsrc"/>.</summary>
    public static IReadOnlyList<ushort> ReadNacks(ReadOnlySpan<byte> compound, uint mediaSsrc)
    {
        // Packet by packet, by their lengths
        List<ushort> lost = [];
        int at = 0;
        while (at + 4 <= compound.Length && compound[at] >> 6 == 2)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(compound[(at + 2)..]) + 1) * 4;
            if (at + length > compound.Length) break;
            if ((compound[at] & 0x1F) == 1 && compound[at + 1] == TransportFeedbackType && length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 8)..]) == mediaSsrc)
            {
                for (int entry = at + 12; entry + 4 <= at + length; entry += 4)
                {
                    ushort id = BinaryPrimitives.ReadUInt16BigEndian(compound[entry..]);
                    ushort mask = BinaryPrimitives.ReadUInt16BigEndian(compound[(entry + 2)..]);
                    lost.Add(id);
                    for (int bit = 0; bit < 16; bit++)
                    {
                        if ((mask & 1 << bit) != 0) lost.Add((ushort)(id + bit + 1));
                    }
                }
            }
            at += length;
        }
        // Return them
        return lost;
    }

    /// <summary>Return a receiver estimated maximum bitrate, <see href="https://datatracker.ietf.org/doc/html/draft-alvestrand-rmcat-remb">REMB</see>, from <paramref name="senderSsrc"/>: the stream <paramref name="mediaSsrc"/> should send at most <paramref name="bitsPerSecond"/>.</summary>
    public static byte[] Remb(uint senderSsrc, uint mediaSsrc, long bitsPerSecond)
    {
        // Payload feedback type 15, no media source, the identifier, then one stream's rate as an 18-bit mantissa and a 6-bit exponent
        byte[] packet = new byte[24];
        Header(packet, 15, PayloadFeedbackType);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);
        "REMB"u8.CopyTo(packet.AsSpan(12));
        long mantissa = Math.Max(0, bitsPerSecond);
        int exponent = 0;
        while (mantissa > 0x3FFFF)
        {
            mantissa >>= 1;
            exponent++;
        }
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 1u << 24 | (uint)exponent << 18 | (uint)mantissa);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), mediaSsrc);
        // Return bytes
        return packet;
    }

    /// <summary>Return the bitrate a REMB in a compound packet asks of <paramref name="mediaSsrc"/>, in bits per second; null when none does.</summary>
    public static long? ReadRemb(ReadOnlySpan<byte> compound, uint mediaSsrc)
    {
        // Packet by packet, by their lengths
        int at = 0;
        while (at + 4 <= compound.Length && compound[at] >> 6 == 2)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(compound[(at + 2)..]) + 1) * 4;
            if (at + length > compound.Length) break;
            if ((compound[at] & 0x1F) == 15 && compound[at + 1] == PayloadFeedbackType && length >= 20 && compound.Slice(at + 12, 4).SequenceEqual("REMB"u8))
            {
                uint rate = BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 16)..]);
                int count = (int)(rate >> 24);
                long bits = (long)(rate & 0x3FFFF) << (int)(rate >> 18 & 0x3F);
                for (int i = 0; i < count && at + 24 + i * 4 <= at + length; i++)
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 20 + i * 4)..]) == mediaSsrc) return bits;
                }
            }
            at += length;
        }
        // On none
        return null;
    }

    /// <summary>Return whether a compound packet asks the sender of <paramref name="mediaSsrc"/> for a keyframe: a picture loss indication, <see href="https://www.rfc-editor.org/rfc/rfc4585#section-6.3.1">RFC 4585 6.3.1</see>, or a full intra request, <see href="https://www.rfc-editor.org/rfc/rfc5104#section-4.3.1">RFC 5104 4.3.1</see>, for that stream.</summary>
    public static bool AsksKeyframe(ReadOnlySpan<byte> compound, uint mediaSsrc)
    {
        // Packet by packet, by their lengths
        int at = 0;
        while (at + 4 <= compound.Length && compound[at] >> 6 == 2)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(compound[(at + 2)..]) + 1) * 4;
            if (at + length > compound.Length) break;
            int format = compound[at] & 0x1F;
            if (compound[at + 1] == PayloadFeedbackType && length >= 12)
            {
                // PLI names the stream after the sender; FIR names it in its first entry
                if (format == 1 && BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 8)..]) == mediaSsrc) return true;
                if (format == 4 && length >= 20 && BinaryPrimitives.ReadUInt32BigEndian(compound[(at + 12)..]) == mediaSsrc) return true;
            }
            at += length;
        }
        // On no request
        return false;
    }

    /// <summary>Write the common header: version 2, the count or format, the type, and the length in 32-bit words less one.</summary>
    private static void Header(byte[] packet, int countOrFormat, byte type)
    {
        // Version, count, type, length
        packet[0] = (byte)(0x80 | countOrFormat);
        packet[1] = type;
        // Done
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
    }
}
