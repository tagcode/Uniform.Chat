namespace Uniform.Matrix.WebRtc.Srtp;

using System.Buffers.Binary;
using System.Security.Cryptography;

/// <summary>One direction of <see href="https://www.rfc-editor.org/rfc/rfc3711">SRTP</see> and SRTCP with the AES_CM_128_HMAC_SHA1_80 suite, which WebRTC requires: protects outgoing packets, or checks and decrypts incoming ones.</summary>
/// <remarks>
/// Keys come from a master key and salt, as DTLS-SRTP exports them, with the key derivation rate 0. Each SSRC keeps its own rollover counter, which extends the 16-bit sequence number to the 48-bit packet index, and a 64-packet replay window.
/// An SRTP packet is the RTP header, the payload encrypted with AES in counter mode, and an 80-bit HMAC-SHA1 tag over the header, the encrypted payload and the rollover counter. SRTCP also encrypts all after the first eight bytes, and appends the E flag and a 31-bit index before its tag.
/// </remarks>
public sealed class SrtpSession
{
    /// <summary>Length of the master key.</summary>
    public const int MasterKeyLength = 16;
    /// <summary>Length of the master salt.</summary>
    public const int MasterSaltLength = 14;
    /// <summary>Length of the authentication tag.</summary>
    public const int TagLength = 10;
    private const int ReplayWindow = 64;
    private readonly Keys rtpKeys;
    private readonly Keys rtcpKeys;
    private readonly Dictionary<uint, RtpState> rtpStates = [];
    private readonly Dictionary<uint, RtcpState> rtcpStates = [];

    /// <summary>Session keys of one of SRTP and SRTCP.</summary>
    private sealed record Keys(Aes Cipher, byte[] Salt, byte[] AuthKey);

    /// <summary>Per SSRC: rollover counter and highest sequence number, and the replay window of received indexes.</summary>
    private sealed class RtpState
    {
        public uint Rollover;
        public ushort HighestSequence;
        public bool Started;
        public ulong Window;
    }

    /// <summary>Per SSRC: the SRTCP index sent last, or the highest received and its replay window.</summary>
    private sealed class RtcpState
    {
        public uint Index;
        public bool Started;
        public ulong Window;
    }

    /// <summary>Derive the session keys of one direction from its master key and salt.</summary>
    public SrtpSession(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt)
    {
        // Assert lengths
        if (masterKey.Length != MasterKeyLength || masterSalt.Length != MasterSaltLength) throw new ArgumentException($"SRTP needs a {MasterKeyLength}-byte master key and a {MasterSaltLength}-byte master salt.");
        // SRTP labels 0 to 2, SRTCP labels 3 to 5
        rtpKeys = DeriveKeys(masterKey, masterSalt, 0);
        rtcpKeys = DeriveKeys(masterKey, masterSalt, 3);
    }

    /// <summary>Return the key, salt or authentication key that <paramref name="label"/> derives, <paramref name="length"/> bytes long, as <see href="https://www.rfc-editor.org/rfc/rfc3711#section-4.3">RFC 3711 4.3</see> does with the rate 0.</summary>
    internal static byte[] Derive(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label, int length)
    {
        // The salt with the label XOR'ed in at byte 7, shifted 16 bits, as the counter's start
        byte[] iv = new byte[16];
        masterSalt.CopyTo(iv);
        iv[7] ^= label;
        using Aes aes = Aes.Create();
        aes.Key = masterKey.ToArray();
        // Return the keystream
        return Keystream(aes, iv, length);
    }

    /// <summary>Return <paramref name="packet"/>, an RTP packet, protected: its payload encrypted and a tag appended.</summary>
    public byte[] ProtectRtp(ReadOnlySpan<byte> packet)
    {
        // Header, and the sequence and SSRC that make the index
        int headerLength = RtpHeaderLength(packet);
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        RtpState state = Get(rtpStates, ssrc);
        if (state.Started && sequence < state.HighestSequence && state.HighestSequence - sequence > 0x8000) state.Rollover++;
        if (!state.Started || IsNewer(sequence, state.HighestSequence)) state.HighestSequence = sequence;
        state.Started = true;

        // Encrypted payload, then the tag over the packet and rollover counter
        byte[] protectedPacket = new byte[packet.Length + TagLength];
        packet.CopyTo(protectedPacket);
        ulong index = ((ulong)state.Rollover << 16) | sequence;
        Crypt(rtpKeys, ssrc, index, protectedPacket.AsSpan(headerLength, packet.Length - headerLength));
        RtpTag(protectedPacket.AsSpan(0, packet.Length), state.Rollover).CopyTo(protectedPacket.AsSpan(packet.Length));
        // Return the SRTP packet
        return protectedPacket;
    }

    /// <summary>Return the RTP packet in <paramref name="packet"/>, an SRTP packet, decrypted; null when its tag is wrong or it was received already.</summary>
    public byte[]? UnprotectRtp(ReadOnlySpan<byte> packet)
    {
        // Assert a header and a tag
        if (packet.Length < 12 + TagLength) return null;
        int headerLength = RtpHeaderLength(packet);
        if (headerLength > packet.Length - TagLength) return null;
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        RtpState state = Get(rtpStates, ssrc);

        // The rollover counter this packet was sent with, by RFC 3711's estimate
        uint rollover = state.Rollover;
        if (state.Started && state.HighestSequence < 0x8000 && sequence - state.HighestSequence > 0x8000) rollover--;
        else if (state.Started && state.HighestSequence >= 0x8000 && state.HighestSequence - 0x8000 > sequence) rollover++;
        ulong index = ((ulong)rollover << 16) | sequence;
        ulong highest = ((ulong)state.Rollover << 16) | state.HighestSequence;

        // Authentic and new, then decrypted
        ReadOnlySpan<byte> authenticated = packet[..^TagLength];
        if (!CryptographicOperations.FixedTimeEquals(RtpTag(authenticated, rollover), packet[^TagLength..])) return null;
        if (state.Started && !Accept(ref state.Window, highest, index)) return null;
        if (!state.Started) state.Window = 1;
        byte[] plain = authenticated.ToArray();
        Crypt(rtpKeys, ssrc, index, plain.AsSpan(headerLength));
        if (!state.Started || index > highest)
        {
            state.Rollover = rollover;
            state.HighestSequence = sequence;
        }
        state.Started = true;
        // Return the RTP packet
        return plain;
    }

    /// <summary>Return <paramref name="packet"/>, a compound RTCP packet, protected: encrypted after its first eight bytes, with the E flag and index, and a tag.</summary>
    public byte[] ProtectRtcp(ReadOnlySpan<byte> packet)
    {
        // Next index of the sender's SSRC
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        RtcpState state = Get(rtcpStates, ssrc);
        uint index = state.Started ? (state.Index + 1) & 0x7FFFFFFF : 1;
        state.Index = index;
        state.Started = true;

        // Encrypted body, E flag and index, then the tag over all of it
        byte[] protectedPacket = new byte[packet.Length + 4 + TagLength];
        packet.CopyTo(protectedPacket);
        Crypt(rtcpKeys, ssrc, index, protectedPacket.AsSpan(8, packet.Length - 8));
        BinaryPrimitives.WriteUInt32BigEndian(protectedPacket.AsSpan(packet.Length), 0x80000000 | index);
        Tag(rtcpKeys.AuthKey, protectedPacket.AsSpan(0, packet.Length + 4), []).CopyTo(protectedPacket.AsSpan(packet.Length + 4));
        // Return the SRTCP packet
        return protectedPacket;
    }

    /// <summary>Return the RTCP packet in <paramref name="packet"/>, an SRTCP packet, decrypted; null when its tag is wrong or it was received already.</summary>
    public byte[]? UnprotectRtcp(ReadOnlySpan<byte> packet)
    {
        // Assert a header, the index word and a tag
        if (packet.Length < 8 + 4 + TagLength) return null;
        ReadOnlySpan<byte> authenticated = packet[..^TagLength];
        if (!CryptographicOperations.FixedTimeEquals(Tag(rtcpKeys.AuthKey, authenticated, []), packet[^TagLength..])) return null;
        uint word = BinaryPrimitives.ReadUInt32BigEndian(authenticated[^4..]);
        uint index = word & 0x7FFFFFFF;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        RtcpState state = Get(rtcpStates, ssrc);

        // New, then decrypted when the E flag says it is encrypted
        if (state.Started && !Accept(ref state.Window, state.Index, index)) return null;
        if (!state.Started) state.Window = 1;
        if (!state.Started || index > state.Index) state.Index = index;
        state.Started = true;
        byte[] plain = authenticated[..^4].ToArray();
        if ((word & 0x80000000) != 0) Crypt(rtcpKeys, ssrc, index, plain.AsSpan(8));
        // Return the RTCP packet
        return plain;
    }

    /// <summary>Return the length of an RTP header: the fixed twelve bytes, the CSRCs, and an extension when the X bit is set.</summary>
    internal static int RtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        // Fixed part and CSRCs
        int length = 12 + 4 * (packet[0] & 0x0F);
        if ((packet[0] & 0x10) == 0 || packet.Length < length + 4) return length;
        // Return with the extension, whose length is in 32-bit words
        return length + 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(length + 2)..]);
    }

    /// <summary>Return the session keys of SRTP, labels from 0, or SRTCP, labels from 3.</summary>
    private static Keys DeriveKeys(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte firstLabel)
    {
        // Cipher key, authentication key and salt
        Aes cipher = Aes.Create();
        cipher.Key = Derive(masterKey, masterSalt, firstLabel, 16);
        // Return keys
        return new Keys(cipher, Derive(masterKey, masterSalt, (byte)(firstLabel + 2), MasterSaltLength), Derive(masterKey, masterSalt, (byte)(firstLabel + 1), 20));
    }

    /// <summary>XOR <paramref name="data"/> with the AES counter-mode keystream of a packet: the salt, the SSRC and the index make the counter's start.</summary>
    private static void Crypt(Keys keys, uint ssrc, ulong index, Span<byte> data)
    {
        // Counter start: salt at bytes 0..13, SSRC XOR'ed at 4..7, index at 8..13
        byte[] iv = new byte[16];
        keys.Salt.CopyTo(iv, 0);
        for (int i = 0; i < 4; i++) iv[4 + i] ^= (byte)(ssrc >> (24 - 8 * i));
        for (int i = 0; i < 6; i++) iv[8 + i] ^= (byte)(index >> (40 - 8 * i));
        byte[] keystream = Keystream(keys.Cipher, iv, data.Length);
        // Done
        for (int i = 0; i < data.Length; i++) data[i] ^= keystream[i];
    }

    /// <summary>Return <paramref name="length"/> bytes of AES counter-mode keystream from the counter <paramref name="iv"/>, whose last 16 bits count blocks.</summary>
    private static byte[] Keystream(Aes aes, byte[] iv, int length)
    {
        // Counter blocks, encrypted in one go
        int blocks = (length + 15) / 16;
        byte[] counters = new byte[blocks * 16];
        for (int block = 0; block < blocks; block++)
        {
            iv.CopyTo(counters, block * 16);
            ushort low = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(iv.AsSpan(14)) + block);
            BinaryPrimitives.WriteUInt16BigEndian(counters.AsSpan(block * 16 + 14), low);
        }
        // Return the keystream, cut to length
        return aes.EncryptEcb(counters, PaddingMode.None)[..length];
    }

    /// <summary>Return the SRTP tag: HMAC-SHA1 over the packet and the rollover counter.</summary>
    private byte[] RtpTag(ReadOnlySpan<byte> packet, uint rollover)
    {
        // Rollover counter in network order after the packet
        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, rollover);
        // Return tag
        return Tag(rtpKeys.AuthKey, packet, counter);
    }

    /// <summary>Return the first 80 bits of HMAC-SHA1 over <paramref name="data"/> and <paramref name="suffix"/>.</summary>
    private static byte[] Tag(byte[] key, ReadOnlySpan<byte> data, ReadOnlySpan<byte> suffix)
    {
        // Both parts, one MAC
        using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        hmac.AppendData(data);
        hmac.AppendData(suffix);
        // Return the truncated tag
        return hmac.GetHashAndReset()[..TagLength];
    }

    /// <summary>Return whether an index is new to the replay window below <paramref name="highest"/>, and mark it seen.</summary>
    private static bool Accept(ref ulong window, ulong highest, ulong index)
    {
        // Newer than all: the window moves on
        if (index > highest)
        {
            ulong shift = index - highest;
            window = shift >= ReplayWindow ? 1 : (window << (int)shift) | 1;
            return true;
        }

        // Too old, or seen
        ulong age = highest - index;
        if (age >= ReplayWindow || (window & (1UL << (int)age)) != 0) return false;
        window |= 1UL << (int)age;
        // Done
        return true;
    }

    /// <summary>Return whether a sequence number is after another, allowing for wrapping.</summary>
    private static bool IsNewer(ushort sequence, ushort than) => (ushort)(sequence - than) is > 0 and < 0x8000;

    /// <summary>Return the state of an SSRC, made on first use.</summary>
    private static T Get<T>(Dictionary<uint, T> states, uint ssrc) where T : new()
    {
        // Existing or new
        if (!states.TryGetValue(ssrc, out T? state)) states[ssrc] = state = new T();
        // Return state
        return state;
    }
}
