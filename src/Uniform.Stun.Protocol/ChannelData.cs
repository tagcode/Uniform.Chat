namespace Uniform.Stun.Protocol;

using System.Buffers.Binary;

/// <summary><see href="https://www.rfc-editor.org/rfc/rfc8656#section-12.4">TURN channel data</see>: a channel number, a length, and the data, with four bytes of overhead instead of a Send or Data indication's thirty-six.</summary>
public static class ChannelData
{
    /// <summary>The first channel number.</summary>
    public const ushort MinChannel = 0x4000;
    /// <summary>The last channel number.</summary>
    public const ushort MaxChannel = 0x4FFF;
    /// <summary>Length of the header: channel number and data length.</summary>
    public const int HeaderLength = 4;

    /// <summary>Return whether <paramref name="channel"/> is a valid channel number.</summary>
    public static bool IsValidChannel(ushort channel) => channel is >= MinChannel and <= MaxChannel;

    /// <summary>Return whether a datagram looks like channel data: its first byte is 64 to 79, as <see href="https://www.rfc-editor.org/rfc/rfc7983">RFC 7983</see> tells it apart.</summary>
    public static bool IsChannelData(ReadOnlySpan<byte> datagram) => datagram.Length >= HeaderLength && datagram[0] is >= 0x40 and <= 0x4F;

    /// <summary>Read channel data; return false when malformed or cut short.</summary>
    /// <param name="message">The bytes, from a datagram or a stream.</param>
    /// <param name="channel">The channel number.</param>
    /// <param name="data">The data, without padding.</param>
    public static bool TryParse(ReadOnlySpan<byte> message, out ushort channel, out ReadOnlySpan<byte> data)
    {
        // Header, then as much data as it says
        channel = 0;
        data = default;
        if (!IsChannelData(message)) return false;
        channel = BinaryPrimitives.ReadUInt16BigEndian(message);
        int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if (HeaderLength + length > message.Length) return false;
        data = message.Slice(HeaderLength, length);
        // Done
        return true;
    }

    /// <summary>Return channel data for <paramref name="channel"/>; over TCP and TLS, <paramref name="padded"/> pads it to four bytes as streams need.</summary>
    public static byte[] Encode(ushort channel, ReadOnlySpan<byte> data, bool padded = false)
    {
        // Header, data, zeros to pad
        int length = HeaderLength + data.Length;
        byte[] message = new byte[padded ? (length + 3) & ~3 : length];
        BinaryPrimitives.WriteUInt16BigEndian(message, channel);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)data.Length);
        data.CopyTo(message.AsSpan(HeaderLength));
        // Return message
        return message;
    }

    /// <summary>Return the length of the STUN message or channel data at the start of a stream's bytes, padding included, or null when fewer than four bytes are there or they are neither.</summary>
    /// <remarks>Over TCP and TLS, STUN messages and padded channel data follow each other; this frames them.</remarks>
    public static int? FrameLength(ReadOnlySpan<byte> stream)
    {
        // Channel data: header and padded data; STUN: header and attributes
        if (stream.Length < HeaderLength) return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(stream[2..]);
        if (stream[0] is >= 0x40 and <= 0x4F) return (HeaderLength + length + 3) & ~3;
        if (stream[0] < 4) return 20 + length;
        // On neither
        return null;
    }
}
