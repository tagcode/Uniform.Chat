namespace Uniform.Matrix.WebRtc.Rtp;

using System.Buffers.Binary;

/// <summary>An <see href="https://www.rfc-editor.org/rfc/rfc3550#section-5.1">RTP</see> packet: the fixed header and the payload; CSRCs and header extensions are skipped when parsed, and none are written.</summary>
public sealed record RtpPacket
{
    /// <summary>Length of the fixed header.</summary>
    public const int HeaderLength = 12;
    /// <summary>The payload type, e.g. 111 for Opus as the SDP maps it.</summary>
    public required byte PayloadType { get; init; }
    /// <summary>The marker bit: for audio, the first packet after silence.</summary>
    public bool Marker { get; init; }
    /// <summary>The sequence number, one more per packet.</summary>
    public required ushort Sequence { get; init; }
    /// <summary>The timestamp in the clock of the payload type, 48 kHz for Opus.</summary>
    public required uint Timestamp { get; init; }
    /// <summary>The synchronization source: identifies the stream.</summary>
    public required uint Ssrc { get; init; }
    /// <summary>The payload.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Return whether a datagram looks like RTP or RTCP: a first byte of 128 to 191, as <see href="https://www.rfc-editor.org/rfc/rfc7983">RFC 7983</see> tells them from STUN and DTLS.</summary>
    public static bool IsRtpOrRtcp(ReadOnlySpan<byte> datagram) => datagram.Length >= 8 && datagram[0] is >= 128 and <= 191;

    /// <summary>Return whether an RTP or RTCP datagram is RTCP: its second byte, as a payload type with the marker, is 192 to 223, as <see href="https://www.rfc-editor.org/rfc/rfc5761#section-4">RFC 5761 4</see> tells them apart when multiplexed.</summary>
    public static bool IsRtcp(ReadOnlySpan<byte> datagram) => datagram.Length >= 2 && datagram[1] is >= 192 and <= 223;

    /// <summary>Parse a packet; null when it is not RTP version 2 or is too short.</summary>
    public static RtpPacket? Parse(ReadOnlySpan<byte> datagram)
    {
        // Version 2, and the header with its CSRCs and extension
        if (datagram.Length < HeaderLength || datagram[0] >> 6 != 2) return null;
        int offset = HeaderLength + 4 * (datagram[0] & 0x0F);
        if ((datagram[0] & 0x10) != 0)
        {
            if (datagram.Length < offset + 4) return null;
            offset += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
        }
        int end = datagram.Length;
        if ((datagram[0] & 0x20) != 0 && end > offset) end -= datagram[^1];
        if (offset > end) return null;
        // Return packet
        return new RtpPacket
        {
            Marker = (datagram[1] & 0x80) != 0,
            PayloadType = (byte)(datagram[1] & 0x7F),
            Sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]),
            Payload = datagram[offset..end].ToArray(),
        };
    }

    /// <summary>Return the packet on the wire.</summary>
    public byte[] Encode()
    {
        // Version 2 without padding, extension or CSRCs
        byte[] datagram = new byte[HeaderLength + Payload.Length];
        datagram[0] = 0x80;
        datagram[1] = (byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), Ssrc);
        Payload.CopyTo(datagram, HeaderLength);
        // Return bytes
        return datagram;
    }
}
