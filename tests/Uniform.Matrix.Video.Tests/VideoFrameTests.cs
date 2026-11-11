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

    /// <summary>Turn a picture by quarter turns: clockwise, the left column becomes the top row; four turns give the picture back.</summary>
    [Fact]
    public void RotatePictures()
    {
        // A 4x2 picture, luma numbered, chroma 2x1
        VideoFrame picture = new(4, 2, [1, 2, 3, 4, 5, 6, 7, 8], [10, 20], [30, 40]) { Rotation = 90 };

        // A quarter turn clockwise: 2x4, the bottom-left pixel on top
        VideoFrame turned = picture.Rotated(90);
        Assert.Equal((2, 4), (turned.Width, turned.Height));
        Assert.Equal([5, 1, 6, 2, 7, 3, 8, 4], turned.Y);
        Assert.Equal([10, 20], turned.U);
        Assert.Equal(0, turned.Rotation);

        // Half and three quarters, and all the way round
        Assert.Equal([8, 7, 6, 5, 4, 3, 2, 1], picture.Rotated(180).Y);
        Assert.Equal([4, 8, 3, 7, 2, 6, 1, 5], picture.Rotated(270).Y);
        // Done
        Assert.Equal(picture.Y, picture.Rotated(90).Rotated(90).Rotated(90).Rotated(90).Y);
    }
}
