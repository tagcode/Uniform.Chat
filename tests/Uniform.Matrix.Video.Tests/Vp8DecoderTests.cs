namespace Uniform.Matrix.Video.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;
using Uniform.Matrix.Video;
using Uniform.Matrix.Video.Vp8;

/// <summary>The VP8 decoder against libvpx's test vectors: every shown frame must have the MD5 libvpx's decoder gives it, over its I420 planes.</summary>
public class Vp8DecoderTests
{
    /// <summary>Numbers of the comprehensive test vectors, 001 to 017.</summary>
    public static TheoryData<int> Vectors => [.. Enumerable.Range(1, 17)];

    /// <summary>Each test vector decodes frame by frame to the expected pictures.</summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void DecodeTestVector(int vector)
    {
        // Arrange: the stream and the MD5 of each shown frame
        string name = Path.Combine(AppContext.BaseDirectory, "TestVectors", $"vp80-00-comprehensive-{vector:000}.ivf");
        string[] expected = [.. File.ReadAllLines(name + ".md5").Where(l => l.Length > 0).Select(l => l.Split(' ')[0])];
        Vp8Decoder decoder = new();
        List<string> actual = [];

        // Act
        foreach (byte[] frame in ReadIvf(File.ReadAllBytes(name)))
        {
            if (decoder.Decode(frame) is VideoFrame picture) actual.Add(Md5(picture));
        }

        // Assert: frame by frame, so a failure names the first wrong one
        for (int i = 0; i < Math.Min(expected.Length, actual.Count); i++) Assert.True(expected[i] == actual[i], $"Frame {i + 1} of vector {vector:000} differs.");
        Assert.Equal(expected.Length, actual.Count);
    }

    /// <summary>A stream must start with a keyframe, and a truncated frame is refused.</summary>
    [Fact]
    public void RefuseBadFrames()
    {
        // Arrange: an inter frame of a vector
        byte[][] frames = [.. ReadIvf(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestVectors", "vp80-00-comprehensive-001.ivf")))];

        // Act and assert
        Assert.Throws<InvalidDataException>(() => new Vp8Decoder().Decode(frames[1]));
        Assert.Throws<InvalidDataException>(() => new Vp8Decoder().Decode(frames[0].AsSpan(0, 20)));
        Assert.NotNull(new Vp8Decoder().Decode(frames[0]));
    }

    /// <summary>Return the frames of an IVF file: a 32-byte header, then per frame its size, a timestamp and the data.</summary>
    internal static IEnumerable<byte[]> ReadIvf(byte[] file)
    {
        // After the header, size-prefixed frames
        Assert.Equal("DKIF"u8.ToArray(), file[..4]);
        int at = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        while (at + 12 <= file.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
            yield return file[(at + 12)..(at + 12 + size)];
            at += 12 + size;
        }
    }

    /// <summary>Return the MD5 of a picture's planes in order, as libvpx's decoder prints it.</summary>
    private static string Md5(VideoFrame picture)
    {
        // Y, U, V, each tightly packed
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(picture.Y);
        md5.AppendData(picture.U);
        md5.AppendData(picture.V);
        // Return hex
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
