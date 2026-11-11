namespace Uniform.LiveKit.Tests;

using System.Text;

/// <summary>Tests of <see cref="FrameCryptor"/>; the expected values come from Python's <c>cryptography</c>, laid out as LiveKit's FrameCryptor.ts does.</summary>
public sealed class FrameCryptorTests
{
    private static readonly byte[] RawKey = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    /// <summary>Derive the AES key of a raw key with HKDF, as LiveKit does.</summary>
    [Fact]
    public void DeriveKeys()
    {
        // Done
        Assert.Equal("4086B4641064E1AE8B63D4EB83AD3E7E", Convert.ToHexString(FrameCryptor.DeriveKey(RawKey)));
    }

    /// <summary>Decrypt frames that WebCrypto's algorithms encrypted, with the clear bytes of each kind and the key index of the trailer.</summary>
    [Fact]
    public void DecryptLiveKitFrames()
    {
        // The key at index 5
        FrameCryptor cryptor = new();
        cryptor.SetKey(5, RawKey);

        // An inter frame of VP8, and Opus
        Assert.Equal([0x31, 0x22, 0x33, .. Encoding.ASCII.GetBytes("hello vp8 inter frame")], cryptor.Decrypt(Convert.FromHexString("312233145f0864a68f1875d33f0f3bd9af4d3359f535da31e24d20c94d25f1285a648d57340397350102030400000a0b000000010c05"), FrameKind.Vp8));
        // Done
        Assert.Equal([0xF8, .. Encoding.ASCII.GetBytes("opus payload")], cryptor.Decrypt(Convert.FromHexString("f8134a117be9df0f7c877007319ea733cd3a17018d091cd9332e1c46e60102030400000a0b000000010c05"), FrameKind.Audio));
    }

    /// <summary>Encrypt and decrypt a keyframe, its first 10 bytes in the clear; refuse a changed header, an unknown key and the server's own frames.</summary>
    [Fact]
    public void EncryptAndRefuse()
    {
        // A keyframe, encrypted with key 3
        FrameCryptor sender = new();
        sender.SetKey(3, RawKey);
        FrameCryptor receiver = new() { ServerTrailer = [0x4C, 0x4B, 0x52, 0x4F, 0x43, 0x4B, 0x53] };
        byte[] keyframe = [0x10, 0x02, 0x00, 0x9D, 0x01, 0x2A, 0x40, 0x00, 0x30, 0x00, .. Encoding.ASCII.GetBytes("the rest of the frame")];
        byte[] encrypted = sender.Encrypt(keyframe, FrameKind.Vp8, 1234, 5678);
        Assert.Equal(keyframe[..10], encrypted[..10]);
        Assert.Equal([12, 3], encrypted[^2..]);

        // No key yet, then the key
        Assert.Null(receiver.Decrypt(encrypted, FrameKind.Vp8));
        receiver.SetKey(3, RawKey);
        Assert.Equal(keyframe, receiver.Decrypt(encrypted, FrameKind.Vp8));
        byte[] changed = [.. encrypted];
        changed[5] ^= 1;
        Assert.Null(receiver.Decrypt(changed, FrameKind.Vp8));
        // Done
        Assert.Null(receiver.Decrypt([1, 2, 3, .. receiver.ServerTrailer], FrameKind.Vp8));
    }

    /// <summary>Encrypt and decrypt with one key on many threads at once, as the microphone and the camera do, and get every frame back.</summary>
    [Fact]
    public async Task EncryptOnManyThreads()
    {
        // One key both ways
        FrameCryptor cryptor = new();
        cryptor.SetKey(0, RawKey);

        // Done: each thread's frames come back as they were
        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (uint i = 0; i < 2000; i++)
            {
                byte[] frame = [0xF8, .. BitConverter.GetBytes(thread), .. BitConverter.GetBytes(i)];
                Assert.Equal(frame, cryptor.Decrypt(cryptor.Encrypt(frame, FrameKind.Audio, (uint)thread, i), FrameKind.Audio));
            }
        }, TestContext.Current.CancellationToken)));
    }
}
