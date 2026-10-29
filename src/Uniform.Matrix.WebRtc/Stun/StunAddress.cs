namespace Uniform.Matrix.WebRtc.Stun;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

/// <summary>The address attributes of STUN: XOR-MAPPED-ADDRESS and the like, whose port and address are XOR'ed with the magic cookie and, for IPv6, the transaction ID, so that middleboxes do not rewrite them.</summary>
public static class StunAddress
{
    private const byte FamilyIPv4 = 0x01;
    private const byte FamilyIPv6 = 0x02;

    /// <summary>Return the value of an XOR'ed address attribute for <paramref name="endPoint"/> in a message with <paramref name="transactionId"/>.</summary>
    public static byte[] EncodeXor(IPEndPoint endPoint, byte[] transactionId)
    {
        // Family, port and address, each XOR'ed
        byte[] address = endPoint.Address.GetAddressBytes();
        byte[] value = new byte[4 + address.Length];
        value[1] = endPoint.AddressFamily == AddressFamily.InterNetworkV6 ? FamilyIPv6 : FamilyIPv4;
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), (ushort)(endPoint.Port ^ (StunMessage.MagicCookie >> 16)));
        byte[] mask = Mask(transactionId);
        for (int i = 0; i < address.Length; i++) value[4 + i] = (byte)(address[i] ^ mask[i]);
        // Return value
        return value;
    }

    /// <summary>Return the address of an XOR'ed address attribute's value, or null when malformed.</summary>
    public static IPEndPoint? DecodeXor(ReadOnlySpan<byte> value, byte[] transactionId)
    {
        // Family and its length
        if (value.Length < 8) return null;
        int length = value[1] switch { FamilyIPv4 => 4, FamilyIPv6 => 16, _ => 0 };
        if (length == 0 || value.Length < 4 + length) return null;

        // Port and address, XOR'ed back
        int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (int)(StunMessage.MagicCookie >> 16);
        byte[] mask = Mask(transactionId);
        byte[] address = new byte[length];
        for (int i = 0; i < length; i++) address[i] = (byte)(value[4 + i] ^ mask[i]);
        // Return endpoint
        return new IPEndPoint(new IPAddress(address), port);
    }

    /// <summary>Return the bytes an address is XOR'ed with: the magic cookie, then the transaction ID.</summary>
    private static byte[] Mask(byte[] transactionId)
    {
        // 4 + 12 bytes
        byte[] mask = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask, StunMessage.MagicCookie);
        transactionId.CopyTo(mask, 4);
        // Return mask
        return mask;
    }
}
