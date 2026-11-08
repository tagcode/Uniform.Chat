namespace Uniform.Matrix.WebRtc.Stun;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>The method of a STUN message: <see href="https://www.rfc-editor.org/rfc/rfc8489">STUN</see>'s Binding, and the <see href="https://www.rfc-editor.org/rfc/rfc8656">TURN</see> methods.</summary>
public enum StunMethod : ushort
{
    /// <summary>Binding: learns the public address, and checks ICE connectivity.</summary>
    Binding = 0x001,
    /// <summary>TURN Allocate: reserves a relayed address.</summary>
    Allocate = 0x003,
    /// <summary>TURN Refresh: keeps an allocation, or ends it.</summary>
    Refresh = 0x004,
    /// <summary>TURN Send indication: data to a peer through the relay.</summary>
    Send = 0x006,
    /// <summary>TURN Data indication: data from a peer through the relay.</summary>
    Data = 0x007,
    /// <summary>TURN CreatePermission: lets a peer's address through the relay.</summary>
    CreatePermission = 0x008,
    /// <summary>TURN ChannelBind: a short channel number for a peer.</summary>
    ChannelBind = 0x009,
}

/// <summary>The class of a STUN message.</summary>
public enum StunClass
{
    /// <summary>A request, which gets a response.</summary>
    Request = 0,
    /// <summary>An indication, which gets none.</summary>
    Indication = 1,
    /// <summary>A success response.</summary>
    Success = 2,
    /// <summary>An error response.</summary>
    Error = 3,
}

/// <summary>A STUN attribute: its type and value.</summary>
/// <param name="Type">Attribute type; see <see cref="StunAttributeTypes"/>.</param>
/// <param name="Value">Value, without padding.</param>
public sealed record StunAttribute(ushort Type, byte[] Value);

/// <summary>Attribute types of STUN, TURN and ICE.</summary>
public static class StunAttributeTypes
{
    /// <summary>Address the server saw, in the clear; older servers.</summary>
    public const ushort MappedAddress = 0x0001;
    /// <summary>User name, e.g. ICE's <c>remote:local</c> fragments.</summary>
    public const ushort Username = 0x0006;
    /// <summary>HMAC-SHA1 over the message.</summary>
    public const ushort MessageIntegrity = 0x0008;
    /// <summary>Error code and reason.</summary>
    public const ushort ErrorCode = 0x0009;
    /// <summary>TURN channel number.</summary>
    public const ushort ChannelNumber = 0x000C;
    /// <summary>TURN allocation lifetime in seconds.</summary>
    public const ushort Lifetime = 0x000D;
    /// <summary>TURN peer address, XOR'ed.</summary>
    public const ushort XorPeerAddress = 0x0012;
    /// <summary>TURN data.</summary>
    public const ushort Data = 0x0013;
    /// <summary>Realm of long-term credentials.</summary>
    public const ushort Realm = 0x0014;
    /// <summary>Nonce of long-term credentials.</summary>
    public const ushort Nonce = 0x0015;
    /// <summary>TURN relayed address, XOR'ed.</summary>
    public const ushort XorRelayedAddress = 0x0016;
    /// <summary>TURN requested transport, 17 for UDP.</summary>
    public const ushort RequestedTransport = 0x0019;
    /// <summary>Address the server saw, XOR'ed with the magic cookie and transaction.</summary>
    public const ushort XorMappedAddress = 0x0020;
    /// <summary>ICE candidate priority.</summary>
    public const ushort Priority = 0x0024;
    /// <summary>ICE nomination by the controlling side.</summary>
    public const ushort UseCandidate = 0x0025;
    /// <summary>Software name.</summary>
    public const ushort Software = 0x8022;
    /// <summary>CRC-32 over the message.</summary>
    public const ushort Fingerprint = 0x8028;
    /// <summary>ICE: the sender is controlled, with its tie breaker.</summary>
    public const ushort IceControlled = 0x8029;
    /// <summary>ICE: the sender is controlling, with its tie breaker.</summary>
    public const ushort IceControlling = 0x802A;
}

/// <summary>A <see href="https://www.rfc-editor.org/rfc/rfc8489">STUN</see> message: method, class, transaction ID and attributes, parsed from or encoded to the wire with optional MESSAGE-INTEGRITY and FINGERPRINT.</summary>
public sealed class StunMessage
{
    /// <summary>The magic cookie every STUN message carries.</summary>
    public const uint MagicCookie = 0x2112A442;
    private const int HeaderLength = 20;
    private const uint FingerprintXor = 0x5354554E;

    /// <summary>Create a message, with a random transaction ID unless given.</summary>
    public StunMessage(StunMethod method, StunClass messageClass, byte[]? transactionId = null)
    {
        // A fresh 96-bit transaction ID for requests
        Method = method;
        Class = messageClass;
        TransactionId = transactionId ?? RandomNumberGenerator.GetBytes(12);
    }

    /// <summary>The method.</summary>
    public StunMethod Method { get; }

    /// <summary>The class.</summary>
    public StunClass Class { get; }

    /// <summary>The 12-byte transaction ID that pairs a response with its request.</summary>
    public byte[] TransactionId { get; }

    /// <summary>The attributes, in order, without MESSAGE-INTEGRITY and FINGERPRINT, which <see cref="Encode"/> adds.</summary>
    public List<StunAttribute> Attributes { get; } = [];

    /// <summary>Return whether a datagram looks like STUN: the first byte, as <see href="https://www.rfc-editor.org/rfc/rfc7983">RFC 7983</see> tells it from DTLS and RTP, and the magic cookie.</summary>
    public static bool IsStun(ReadOnlySpan<byte> datagram) => datagram.Length >= HeaderLength && datagram[0] < 4 && BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) == MagicCookie;

    /// <summary>Parse a message; null when it is not STUN or is malformed. MESSAGE-INTEGRITY and FINGERPRINT stay among the attributes, to check with <see cref="CheckIntegrity"/> and <see cref="CheckFingerprint"/>.</summary>
    public static StunMessage? Parse(ReadOnlySpan<byte> datagram)
    {
        // Header: type, length, cookie, transaction
        if (!IsStun(datagram)) return null;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        int length = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        if (length % 4 != 0 || HeaderLength + length > datagram.Length) return null;
        StunMessage message = new(DecodeMethod(type), DecodeClass(type), datagram.Slice(8, 12).ToArray());

        // Attributes, each padded to four bytes
        int offset = HeaderLength;
        while (offset + 4 <= HeaderLength + length)
        {
            ushort attributeType = BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            if (offset + 4 + valueLength > HeaderLength + length) return null;
            message.Attributes.Add(new StunAttribute(attributeType, datagram.Slice(offset + 4, valueLength).ToArray()));
            offset += 4 + Pad(valueLength);
        }
        // Return message
        return message;
    }

    /// <summary>Return the message on the wire, followed by a MESSAGE-INTEGRITY with <paramref name="integrityKey"/> when given, and a FINGERPRINT when asked.</summary>
    /// <param name="integrityKey">Key of the HMAC-SHA1: an ICE password's bytes, or a long-term key from <see cref="LongTermKey"/>.</param>
    /// <param name="fingerprint">Whether to end with a FINGERPRINT, as ICE requires.</param>
    public byte[] Encode(byte[]? integrityKey = null, bool fingerprint = false)
    {
        // Header, then attributes padded with zeros
        List<byte> bytes = new(HeaderLength + Attributes.Sum(a => 4 + Pad(a.Value.Length)) + 32);
        Span<byte> header = stackalloc byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header, EncodeType(Method, Class));
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], MagicCookie);
        TransactionId.CopyTo(header[8..]);
        bytes.AddRange(header.ToArray());
        foreach (StunAttribute attribute in Attributes.Where(a => a.Type is not (StunAttributeTypes.MessageIntegrity or StunAttributeTypes.Fingerprint))) AppendAttribute(bytes, attribute.Type, attribute.Value);

        // Integrity over all before it, the length counting it
        if (integrityKey is not null)
        {
            SetLength(bytes, bytes.Count - HeaderLength + 24);
            AppendAttribute(bytes, StunAttributeTypes.MessageIntegrity, HMACSHA1.HashData(integrityKey, bytes.ToArray()));
        }

        // Fingerprint over all before it, the length counting it
        if (fingerprint)
        {
            SetLength(bytes, bytes.Count - HeaderLength + 8);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(bytes.ToArray()) ^ FingerprintXor);
            AppendAttribute(bytes, StunAttributeTypes.Fingerprint, crc);
        }
        SetLength(bytes, bytes.Count - HeaderLength);
        // Return bytes
        return [.. bytes];
    }

    /// <summary>Return whether <paramref name="datagram"/>, this message as received, has a MESSAGE-INTEGRITY made with <paramref name="key"/>.</summary>
    public static bool CheckIntegrity(ReadOnlySpan<byte> datagram, byte[] key)
    {
        // The attribute's offset; the HMAC covers what comes before, with the length up to and including it
        if (FindAttribute(datagram, StunAttributeTypes.MessageIntegrity) is not int offset) return false;
        byte[] covered = datagram[..offset].ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(covered.AsSpan(2), (ushort)(offset - HeaderLength + 24));
        // Return whether it matches, in constant time
        return CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(key, covered), datagram.Slice(offset + 4, 20));
    }

    /// <summary>Return whether <paramref name="datagram"/> ends with a correct FINGERPRINT.</summary>
    public static bool CheckFingerprint(ReadOnlySpan<byte> datagram)
    {
        // The CRC covers all before the attribute, with the length as sent
        if (FindAttribute(datagram, StunAttributeTypes.Fingerprint) is not int offset || offset + 8 != datagram.Length) return false;
        // Return whether it matches
        return BinaryPrimitives.ReadUInt32BigEndian(datagram[(offset + 4)..]) == (Crc32.Compute(datagram[..offset]) ^ FingerprintXor);
    }

    /// <summary>Return the key of <see href="https://www.rfc-editor.org/rfc/rfc8489#section-9.2.2">long-term credentials</see>, as TURN servers use: MD5 of <c>username:realm:password</c>. The inputs must already be SASLprep'ed; plain ASCII is.</summary>
    public static byte[] LongTermKey(string username, string realm, string password) => MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));

    /// <summary>Return the first value of an attribute type, or null.</summary>
    public byte[]? Get(ushort type) => Attributes.FirstOrDefault(a => a.Type == type)?.Value;

    /// <summary>Return the first value of an attribute type as UTF-8 text, or null.</summary>
    public string? GetText(ushort type) => Get(type) is byte[] value ? Encoding.UTF8.GetString(value) : null;

    /// <summary>The code of the ERROR-CODE attribute, e.g. 401, or null when there is none.</summary>
    public int? ErrorCode => Get(StunAttributeTypes.ErrorCode) is { Length: >= 4 } value ? (value[2] & 0x07) * 100 + value[3] : null;

    /// <summary>Add an attribute.</summary>
    public StunMessage Add(ushort type, byte[] value)
    {
        // In order
        Attributes.Add(new StunAttribute(type, value));
        // Return this, to chain
        return this;
    }

    /// <summary>Add a UTF-8 text attribute.</summary>
    public StunMessage Add(ushort type, string value) => Add(type, Encoding.UTF8.GetBytes(value));

    /// <summary>Return the attribute's offset in a raw message, or null.</summary>
    private static int? FindAttribute(ReadOnlySpan<byte> datagram, ushort type)
    {
        // Walk the attributes as sent
        if (!IsStun(datagram)) return null;
        int end = Math.Min(datagram.Length, HeaderLength + BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]));
        for (int offset = HeaderLength; offset + 4 <= end; offset += 4 + Pad(BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..])))
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]) == type) return offset;
        }
        // Return none
        return null;
    }

    /// <summary>Append an attribute, padded with zeros to four bytes.</summary>
    private static void AppendAttribute(List<byte> bytes, ushort type, byte[] value)
    {
        // Type, length, value, padding
        bytes.Add((byte)(type >> 8));
        bytes.Add((byte)type);
        bytes.Add((byte)(value.Length >> 8));
        bytes.Add((byte)value.Length);
        bytes.AddRange(value);
        for (int i = value.Length; i < Pad(value.Length); i++) bytes.Add(0);
    }

    /// <summary>Set the length field of the header.</summary>
    private static void SetLength(List<byte> bytes, int length)
    {
        // Big-endian
        bytes[2] = (byte)(length >> 8);
        bytes[3] = (byte)length;
    }

    /// <summary>Return a length rounded up to four bytes.</summary>
    private static int Pad(int length) => (length + 3) & ~3;

    /// <summary>Return the message type of a method and class, whose bits interleave.</summary>
    private static ushort EncodeType(StunMethod method, StunClass messageClass)
    {
        // M11..M7, C1, M6..M4, C0, M3..M0
        int m = (int)method, c = (int)messageClass;
        // Return type
        return (ushort)((m & 0x000F) | ((m & 0x0070) << 1) | ((m & 0x0F80) << 2) | ((c & 1) << 4) | ((c & 2) << 7));
    }

    /// <summary>Return the method of a message type.</summary>
    private static StunMethod DecodeMethod(ushort type) => (StunMethod)((type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2));

    /// <summary>Return the class of a message type.</summary>
    private static StunClass DecodeClass(ushort type) => (StunClass)(((type >> 4) & 1) | ((type >> 7) & 2));
}
