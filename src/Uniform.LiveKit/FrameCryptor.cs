namespace Uniform.LiveKit;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>The kind of a frame, which decides how many of its first bytes stay unencrypted, so that an SFU and the packetizer still read them.</summary>
public enum FrameKind
{
    /// <summary>An Opus frame: its first byte, the TOC, stays.</summary>
    Audio,
    /// <summary>A VP8 frame: 10 bytes of a keyframe, its tag and size, or 3 of an inter frame, its tag, stay.</summary>
    Vp8,
}

/// <summary>LiveKit's end-to-end encryption of media frames, as its clients and Element Call do it: AES-GCM with a key derived by HKDF, the first bytes of each frame in the clear and authenticated, and the IV and the key's index after the frame.</summary>
/// <remarks>
/// A frame is <c>header ‖ AES-GCM(ciphertext ‖ tag) ‖ IV(12) ‖ 12 ‖ key index</c>. The header is the frame's first bytes, as <see cref="FrameKind"/> says, and the additional data of AES-GCM.
/// A raw key, e.g. one a member sent in <c>io.element.call.encryption_keys</c>, becomes an AES-128 key with HKDF-SHA-256, the salt <c>LKFrameEncryptionKey</c> and 128 zero bytes of info.
/// </remarks>
/// <see href="https://github.com/livekit/client-sdk-js/blob/main/src/e2ee/worker/FrameCryptor.ts"/>
public sealed class FrameCryptor
{
    /// <summary>Length of the IV after each frame.</summary>
    public const int IvLength = 12;
    /// <summary>Length of the AES-GCM tag.</summary>
    public const int TagLength = 16;
    /// <summary>The salt of the key derivation.</summary>
    public static readonly byte[] Salt = Encoding.UTF8.GetBytes("LKFrameEncryptionKey");
    private readonly AesGcm?[] keys = new AesGcm?[256];
    private readonly Lock gate = new();
    private uint counter = (uint)Random.Shared.Next();

    /// <summary>The index of the key frames are encrypted with, set by <see cref="SetKey"/>.</summary>
    public byte CurrentIndex { get; private set; }

    /// <summary>The trailer of frames an SFU injects, e.g. silence while muted, which come unencrypted; empty for none.</summary>
    public byte[] ServerTrailer { get; set; } = [];

    /// <summary>Frames that did not decrypt, e.g. before their key came.</summary>
    public long Failures => Interlocked.Read(ref failures);
    private long failures;

    /// <summary>Return the AES-128 key of a raw key, as LiveKit derives it.</summary>
    public static byte[] DeriveKey(ReadOnlySpan<byte> rawKey)
    {
        // HKDF-SHA-256 to 16 bytes
        byte[] key = new byte[16];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, rawKey, key, Salt, new byte[128]);
        // Return key
        return key;
    }

    /// <summary>Set the raw key of <paramref name="index"/>; with <paramref name="current"/>, frames are encrypted with it from now on.</summary>
    public void SetKey(byte index, ReadOnlySpan<byte> rawKey, bool current = true)
    {
        // Derived, replacing the earlier key of the index
        AesGcm key = new(DeriveKey(rawKey), TagLength);
        lock (gate)
        {
            keys[index]?.Dispose();
            keys[index] = key;
            // Done
            if (current) CurrentIndex = index;
        }
    }

    /// <summary>Return whether a key of <paramref name="index"/> is set.</summary>
    public bool HasKey(byte index) { lock (gate) return keys[index] is not null; }

    /// <summary>Return how many first bytes of <paramref name="frame"/> stay in the clear.</summary>
    public static int ClearLength(ReadOnlySpan<byte> frame, FrameKind kind) => Math.Min(frame.Length, kind switch
    {
        FrameKind.Audio => 1,
        _ => frame.Length > 0 && (frame[0] & 1) == 0 ? 10 : 3,
    });

    /// <summary>Return <paramref name="frame"/> encrypted with the current key; the IV takes the stream's <paramref name="ssrc"/>, the frame's <paramref name="timestamp"/> and a counter, so it never repeats.</summary>
    /// <exception cref="InvalidOperationException">When no key is set.</exception>
    public byte[] Encrypt(ReadOnlySpan<byte> frame, FrameKind kind, uint ssrc, uint timestamp)
    {
        // Assert a key; empty frames go as they are
        if (frame.Length == 0) return [];
        AesGcm key;
        byte index;
        uint count;
        lock (gate)
        {
            index = CurrentIndex;
            key = keys[index] ?? throw new InvalidOperationException("No media key is set.");
            count = counter++;
        }

        // Header, ciphertext and tag, IV, trailer
        int clear = ClearLength(frame, kind);
        int body = frame.Length - clear;
        byte[] output = new byte[clear + body + TagLength + IvLength + 2];
        frame[..clear].CopyTo(output);
        Span<byte> iv = output.AsSpan(clear + body + TagLength, IvLength);
        BinaryPrimitives.WriteUInt32BigEndian(iv, ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(iv[8..], count);
        key.Encrypt(iv, frame[clear..], output.AsSpan(clear, body), output.AsSpan(clear + body, TagLength), frame[..clear]);
        output[^2] = IvLength;
        output[^1] = index;
        // Return frame
        return output;
    }

    /// <summary>Return <paramref name="frame"/> decrypted with the key its trailer names; null when it does not decrypt, has no such key, or is a frame the SFU injected.</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> frame, FrameKind kind)
    {
        // Assert a trailer, and not the SFU's own frame
        if (ServerTrailer.Length > 0 && frame.EndsWith(ServerTrailer)) return null;
        int clear = ClearLength(frame, kind);
        if (frame.Length < clear + TagLength + 2 || frame[^2] != IvLength || frame.Length < clear + TagLength + IvLength + 2) return Fail();
        AesGcm? key;
        lock (gate) key = keys[frame[^1]];
        if (key is null) return Fail();

        // Header, then the decrypted body
        int body = frame.Length - clear - TagLength - IvLength - 2;
        byte[] output = new byte[clear + body];
        frame[..clear].CopyTo(output);
        try
        {
            key.Decrypt(frame.Slice(clear + body + TagLength, IvLength), frame.Slice(clear, body), frame.Slice(clear + body, TagLength), output.AsSpan(clear), frame[..clear]);
        }
        catch (AuthenticationTagMismatchException)
        {
            // On a wrong key or a changed frame
            return Fail();
        }
        // Return frame
        return output;
    }

    /// <summary>Count a failure, and return null.</summary>
    private byte[]? Fail()
    {
        // Done
        Interlocked.Increment(ref failures);
        return null;
    }
}
