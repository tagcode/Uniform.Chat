namespace Uniform.Matrix.Hosting.Tests;

using Uniform.Matrix.Hosting.Imaging;
using Uniform.Matrix.Testing;

/// <summary>Tests of <see cref="ImageOrientation"/>: reading a photo's Exif orientation and turning its pixels upright.</summary>
public class ImageOrientationTests
{
    /// <summary>The smallest JPEG framing: a start and an end marker.</summary>
    private static readonly byte[] EmptyJpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    /// <summary>The orientation is read in either TIFF byte order, from the bytes or a stream; files without one, and other formats, show as stored.</summary>
    [Fact]
    public void ReadOrientation()
    {
        // Both byte orders, every value
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            Assert.Equal(orientation, ImageOrientation.Read(ExifJpeg.WithOrientation(EmptyJpeg, orientation)));
            Assert.Equal(orientation, ImageOrientation.Read(new MemoryStream(ExifJpeg.WithOrientation(EmptyJpeg, orientation, littleEndian: false))));
        }

        // Done: none, out of range, cut short, not a JPEG
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(EmptyJpeg));
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(ExifJpeg.WithOrientation(EmptyJpeg, 9)));
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(ExifJpeg.WithOrientation(EmptyJpeg, 6).AsSpan(0, 20)));
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read("\x89PNG\r\n\x1a\n"u8));
    }

    /// <summary>Each orientation turns or mirrors a 3×2 image as the Exif specification shows it, swapping the sides for quarter turns.</summary>
    [Theory]
    [InlineData(1, 3, 2, "ABC|DEF")]
    [InlineData(2, 3, 2, "CBA|FED")]
    [InlineData(3, 3, 2, "FED|CBA")]
    [InlineData(4, 3, 2, "DEF|ABC")]
    [InlineData(5, 2, 3, "AD|BE|CF")]
    [InlineData(6, 2, 3, "DA|EB|FC")]
    [InlineData(7, 2, 3, "FC|EB|DA")]
    [InlineData(8, 2, 3, "CF|BE|AD")]
    public void TurnUpright(int orientation, int expectedWidth, int expectedHeight, string expected)
    {
        // Stored rows ABC over DEF, a letter per 4-byte pixel
        byte[] pixels = [.. "ABCDEF".SelectMany(c => new[] { (byte)c, (byte)0, (byte)0, (byte)255 })];

        // Act
        (byte[] turned, int width, int height) = ImageOrientation.Apply(pixels, 3, 2, orientation);

        // Assert
        Assert.Equal((expectedWidth, expectedHeight), (width, height));
        string shown = string.Join('|', Enumerable.Range(0, height).Select(y => new string([.. Enumerable.Range(0, width).Select(x => (char)turned[(y * width + x) * 4])])));
        Assert.Equal(expected, shown);
    }
}
