namespace Uniform.Matrix.WebRtc.Rtp;

using System.Buffers.Binary;

/// <summary>An <see href="https://www.rfc-editor.org/rfc/rfc3550#section-5.1">RTP</see> packet: the fixed header, header extensions of the <see href="https://www.rfc-editor.org/rfc/rfc8285#section-4.2">one-byte form</see>, and the payload; CSRCs are skipped when parsed.</summary>
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
    /// <summary>Header extensions by their ID, 1 to 14, as the SDP's <c>extmap</c> maps them, each 1 to 16 bytes; empty for none.</summary>
    public IReadOnlyList<(byte Id, byte[] Data)> Extensions { get; init; } = [];

    /// <summary>Return the data of extension <paramref name="id"/>, or null.</summary>
    public byte[]? Extension(byte id) => Extensions.FirstOrDefault(e => e.Id == id).Data;

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
        List<(byte, byte[])> extensions = [];
        if ((datagram[0] & 0x10) != 0)
        {
            if (datagram.Length < offset + 4) return null;
            int length = 4 * BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            if (datagram.Length < offset + 4 + length) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]) == 0xBEDE) ReadOneByte(datagram.Slice(offset + 4, length), extensions);
            offset += 4 + length;
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
            Extensions = extensions,
        };
    }

    /// <summary>Read one-byte extension elements: a byte of ID and length less one, then the data; padding bytes of 0 between them; ID 15 ends them.</summary>
    private static void ReadOneByte(ReadOnlySpan<byte> block, List<(byte, byte[])> extensions)
    {
        // Element by element
        for (int at = 0; at < block.Length;)
        {
            byte header = block[at++];
            if (header == 0) continue;
            int id = header >> 4, length = (header & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) return;
            extensions.Add(((byte)id, block.Slice(at, length).ToArray()));
            at += length;
        }
    }

    /// <summary>Return the packet on the wire.</summary>
    public byte[] Encode()
    {
        // Version 2 without padding or CSRCs; extensions in the one-byte form, padded to 32 bits
        int elements = Extensions.Sum(e => 1 + e.Data.Length);
        int extension = Extensions.Count == 0 ? 0 : 4 + (elements + 3) / 4 * 4;
        byte[] datagram = new byte[HeaderLength + extension + Payload.Length];
        datagram[0] = (byte)(Extensions.Count == 0 ? 0x80 : 0x90);
        if (extension > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(HeaderLength), 0xBEDE);
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(HeaderLength + 2), (ushort)((extension - 4) / 4));
            int at = HeaderLength + 4;
            foreach ((byte id, byte[] data) in Extensions)
            {
                datagram[at++] = (byte)(id << 4 | (data.Length - 1));
                data.CopyTo(datagram, at);
                at += data.Length;
            }
        }
        datagram[1] = (byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), Ssrc);
        Payload.CopyTo(datagram, HeaderLength + extension);
        // Return bytes
        return datagram;
    }
}
