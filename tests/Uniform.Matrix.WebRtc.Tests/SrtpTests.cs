namespace Uniform.Matrix.WebRtc.Tests;

using Uniform.Matrix.WebRtc.Srtp;

/// <summary>Tests of <see cref="SrtpSession"/> against the vectors of RFC 3711 and libsrtp.</summary>
public class SrtpTests
{
    private const string MasterKey = "E1F97A0D3E018BE0D64FA32C06DE4139";
    private const string MasterSalt = "0EC675AD498AFEEBB6960B3AABE6";
    private const string Rtp = "800f1234decafbadcafebabeabababababababababababababababab";
    private const string Srtp = "800f1234decafbadcafebabe4e55dc4ce79978d88ca4d215949d2402b78d6acc99ea179b8dbb";
    private const string Rtcp = "81c8000bcafebabeabababababababababababababababab";
    private const string Srtcp = "81c8000bcafebabe7128035be487b9bdbef89041f977a5a880000001993e08cd54d6c1230798";

    /// <summary>Derive the session keys of RFC 3711 B.3.</summary>
    [Fact]
    public void DeriveKeys()
    {
        // Cipher key, salt and authentication key
        byte[] key = Convert.FromHexString(MasterKey), salt = Convert.FromHexString(MasterSalt);
        Assert.Equal("C61E7A93744F39EE10734AFE3FF7A087", Convert.ToHexString(SrtpSession.Derive(key, salt, 0, 16)));
        Assert.Equal("30CBBC08863D8C85D49DB34A9AE1", Convert.ToHexString(SrtpSession.Derive(key, salt, 2, 14)));
        Assert.Equal("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4", Convert.ToHexString(SrtpSession.Derive(key, salt, 1, 20)));
    }

    /// <summary>Protect the RTP and RTCP packets of libsrtp's validation as libsrtp does.</summary>
    [Fact]
    public void ProtectPackets()
    {
        // Same bytes as libsrtp
        SrtpSession session = New();
        Assert.Equal(Srtp, Convert.ToHexString(session.ProtectRtp(Convert.FromHexString(Rtp))), ignoreCase: true);
        Assert.Equal(Srtcp, Convert.ToHexString(session.ProtectRtcp(Convert.FromHexString(Rtcp))), ignoreCase: true);
    }

    /// <summary>Unprotect libsrtp's packets once, and refuse replays and tampering.</summary>
    [Fact]
    public void UnprotectPackets()
    {
        // Decrypted once
        SrtpSession session = New();
        Assert.Equal(Rtp, Convert.ToHexString(session.UnprotectRtp(Convert.FromHexString(Srtp))!), ignoreCase: true);
        Assert.Equal(Rtcp, Convert.ToHexString(session.UnprotectRtcp(Convert.FromHexString(Srtcp))!), ignoreCase: true);
        Assert.Null(session.UnprotectRtp(Convert.FromHexString(Srtp)));
        Assert.Null(session.UnprotectRtcp(Convert.FromHexString(Srtcp)));

        // A changed byte fails the tag
        byte[] tampered = Convert.FromHexString(Srtp);
        tampered[20] ^= 1;
        Assert.Null(New().UnprotectRtp(tampered));
    }

    /// <summary>Carry packets across the sequence number's wrap, and accept late ones within the window.</summary>
    [Fact]
    public void WrapSequence()
    {
        // Sender and receiver with the same keys
        SrtpSession sender = New(), receiver = New();
        List<byte[]> sent = [];
        for (int i = 0; i < 10; i++) sent.Add(sender.ProtectRtp(Packet((ushort)(0xFFFB + i))));

        // In order except two swapped, across the wrap
        foreach (int i in new[] { 0, 1, 2, 3, 5, 4, 6, 7, 8, 9 })
        {
            byte[]? plain = receiver.UnprotectRtp(sent[i]);
            Assert.NotNull(plain);
            Assert.Equal(Packet((ushort)(0xFFFB + i)), plain);
        }
        // Done
        Assert.Null(receiver.UnprotectRtp(sent[4]));
    }

    /// <summary>Return a session with libsrtp's test key.</summary>
    private static SrtpSession New() => new(Convert.FromHexString(MasterKey), Convert.FromHexString(MasterSalt));

    /// <summary>Return an RTP packet with a sequence number and a payload.</summary>
    private static byte[] Packet(ushort sequence)
    {
        // Header with the sequence, then payload
        byte[] packet = Convert.FromHexString(Rtp);
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        // Return packet
        return packet;
    }
}
