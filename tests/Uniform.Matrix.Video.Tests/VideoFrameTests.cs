namespace Uniform.Matrix.Video.Tests;

/// <summary>Converting decoded pictures to BGRA for display.</summary>
public class VideoFrameTests
{
    /// <summary>BT.601 limited-range white, black, red, green and blue convert to their BGRA colors, each pixel with the chroma of its 2×2 block, rows at the stride.</summary>
    [Fact]
    public void ConvertToBgra()
    {
        // Arrange: a 3x2 picture whose two chroma blocks are white and red, the second only one pixel wide
        byte[] y = [235, 16, 81, 235, 16, 81];
        VideoFrame frame = new(3, 2, y, [128, 90], [128, 240]);
        byte[] bgra = new byte[2 * 16];

        // Act: rows of 16 bytes, 12 used
        frame.CopyToBgra(bgra, 16);

        // Assert: white and black from neutral chroma, then red; the padding untouched
        Assert.Equal([255, 255, 255, 255], bgra[0..4]);
        Assert.Equal([0, 0, 0, 255], bgra[4..8]);
        Assert.Equal([0, 0, 255, 255], bgra[8..12]);
        Assert.Equal([0, 0, 0, 0], bgra[12..16]);
        Assert.Equal(bgra[0..12], bgra[16..28]);
        Assert.InRange(Convert(145, 54, 34)[1], 250, 255);
        // Done
        Assert.InRange(Convert(41, 240, 110)[0], 250, 255);
    }

    /// <summary>Refuse a destination shorter than the picture.</summary>
    [Fact]
    public void RefuseSmallDestination()
    {
        // Arrange
        VideoFrame frame = new(2, 2, new byte[4], new byte[1], new byte[1]);
        // Done
        Assert.Throws<ArgumentException>(() => frame.CopyToBgra(new byte[15], 8));
    }

    /// <summary>Return the BGRA of one pixel of luma <paramref name="y"/> and chroma <paramref name="u"/>, <paramref name="v"/>.</summary>
    private static byte[] Convert(byte y, byte u, byte v)
    {
        // One pixel
        byte[] bgra = new byte[4];
        new VideoFrame(1, 1, [y], [u], [v]).CopyToBgra(bgra, 4);
        // Return it
        return bgra;
    }
}
