namespace Uniform.Matrix.Client.Terminal.Tests;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Uniform.Matrix.Client.Terminal.Ui.Images;

/// <summary>Unit tests for <see cref="DecodedImage"/> and <see cref="AsciiArt"/>.</summary>
public class ImageTests
{
    /// <summary>Create an image from a function of pixel position.</summary>
    private static DecodedImage Create(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        // Fill RGBA rows
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);
                int offset = (y * width + x) * 4;
                (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]) = (r, g, b, a);
            }
        // Return image
        return new DecodedImage(width, height, rgba);
    }

    /// <summary>Encode RGBA pixels as a minimal PNG: one IDAT chunk, no filtering.</summary>
    private static byte[] EncodePng(DecodedImage image)
    {
        // Rows prefixed with filter type 0, deflated with zlib
        using MemoryStream raw = new();
        for (int y = 0; y < image.Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(image.Rgba, y * image.Width * 4, image.Width * 4);
        }
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zlib);

        // Signature, header, data, end
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = 6;
        using MemoryStream png = new();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        // Return file bytes
        return png.ToArray();
    }

    /// <summary>Write one PNG chunk with its length and CRC.</summary>
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        // Length, type, data, CRC over type and data
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        stream.Write(buffer);
    }

    /// <summary>Compute the CRC-32 used by PNG.</summary>
    private static uint Crc32(byte[] data)
    {
        // Bitwise CRC-32, polynomial 0xEDB88320
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        // Return final value
        return ~crc;
    }

    /// <summary>A PNG decodes to the same pixels.</summary>
    [Fact]
    public void CanDecodePng()
    {
        // Arrange
        DecodedImage source = Create(3, 2, (x, y) => ((byte)(x * 100), (byte)(y * 200), 7, 255));

        // Act
        DecodedImage? decoded = DecodedImage.TryDecode(EncodePng(source));

        // Assert
        Assert.NotNull(decoded);
        Assert.Equal((3, 2), (decoded.Width, decoded.Height));
        Assert.Equal(source.Rgba, decoded.Rgba);
    }

    /// <summary>Data that is not an image is rejected without throwing.</summary>
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void CanRejectNonImage(byte[] data)
    {
        // Act and assert
        Assert.Null(DecodedImage.TryDecode(data));
    }

    /// <summary>Black is blank and white is dense on a dark background, reversed on a light one; transparent stays blank.</summary>
    [Fact]
    public void CanMapBrightnessToCharacters()
    {
        // Arrange: left half white, right half black, bottom third transparent; 4×6 pixels make 4×3 cells
        DecodedImage image = Create(4, 6, (x, y) => y >= 4 ? ((byte)255, (byte)255, (byte)255, (byte)0) : x < 2 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255));

        // Act
        IReadOnlyList<string> dark = AsciiArt.Render(image, 4, 3, darkBackground: true, style: ImagePreviewStyle.Ascii);
        IReadOnlyList<string> light = AsciiArt.Render(image, 4, 3, darkBackground: false, style: ImagePreviewStyle.Ascii);

        // Assert: densest ramp character on drawn pixels
        string dense = new(AsciiArt.AsciiRamp[^1], 2);
        Assert.Equal(dense, dark[0]);
        Assert.Equal("  " + dense, light[0]);
        Assert.Equal("", dark[^1]);
        Assert.Equal("", light[^1]);
    }

    /// <summary>Fitting keeps proportions on cells twice as tall as wide and respects both limits.</summary>
    [Theory]
    [InlineData(100, 100, 48, 12, 24, 12)]
    [InlineData(400, 100, 48, 12, 48, 6)]
    [InlineData(10, 10, 48, 12, 20, 10)]
    [InlineData(1000, 500, 300, 80, 300, 75)]
    [InlineData(100, 1000, 48, 12, 2, 12)]
    public void CanFitCells(int width, int height, int maxColumns, int maxRows, int columns, int rows)
    {
        // Act and assert
        Assert.Equal((columns, rows), AsciiArt.FitCells(width, height, maxColumns, maxRows));
    }

    /// <summary>Rendering never exceeds the box.</summary>
    [Fact]
    public void CanStayWithinBox()
    {
        // Arrange: gradient
        DecodedImage image = Create(320, 240, (x, y) => ((byte)(x * 255 / 319), (byte)(y * 255 / 239), 128, 255));

        // Act
        IReadOnlyList<string> lines = AsciiArt.Render(image, 40, 10);

        // Assert: default style uses shade blocks only
        Assert.InRange(lines.Count, 1, 10);
        Assert.All(lines, l => Assert.InRange(l.Length, 0, 40));
        Assert.All(lines.SelectMany(l => l), c => Assert.Contains(c, AsciiArt.BlockRamp));
    }

    /// <summary>Downscaling bounds both sides and keeps the aspect ratio.</summary>
    [Fact]
    public void CanDownscale()
    {
        // Arrange
        DecodedImage image = Create(400, 200, (_, _) => (1, 2, 3, 255));

        // Act
        DecodedImage small = image.Downscale(100);

        // Assert
        Assert.Equal((100, 50), (small.Width, small.Height));
        Assert.Equal((1, 2, 3, 255), small.GetPixel(99, 49));
        Assert.Same(small, small.Downscale(100));
    }

    /// <summary>Braille fills all eight dots on bright cells, none on dark ones, and dithers gray to a partial pattern.</summary>
    [Fact]
    public void CanRenderBraille()
    {
        // Arrange: white left third, mid-gray middle third, black right third; 6×8 pixels make 3×2 cells
        DecodedImage image = Create(60, 40, (x, _) => x < 20 ? ((byte)255, (byte)255, (byte)255, (byte)255) : x < 40 ? ((byte)128, (byte)128, (byte)128, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255));

        // Act
        IReadOnlyList<string> lines = AsciiArt.Render(image, 30, 10, style: ImagePreviewStyle.Braille);

        // Assert full dots left, partial dots in the middle, nothing right
        string row = lines[0];
        Assert.Equal('⣿', row[0]);
        Assert.All(row.Take(row.Length), c => Assert.True(c == ' ' || c is >= '\u2800' and <= '\u28FF'));
        int middleDots = row.Skip(10).Take(10).Sum(c => c == ' ' ? 0 : int.PopCount(c - 0x2800));
        Assert.InRange(middleDots, 20, 60);
        Assert.True(row.Length <= 20, "black third is trimmed");
    }

    /// <summary>A dim, low-contrast image is stretched to use the whole ramp.</summary>
    [Fact]
    public void CanStretchContrast()
    {
        // Arrange: two dim grays
        DecodedImage image = Create(8, 4, (x, _) => x < 4 ? ((byte)25, (byte)25, (byte)25, (byte)255) : ((byte)75, (byte)75, (byte)75, (byte)255));

        // Act
        string line = AsciiArt.Render(image, 8, 4, style: ImagePreviewStyle.Ascii)[0];

        // Assert darkest becomes blank, brightest becomes densest
        Assert.Equal(new string(AsciiArt.AsciiRamp[^1], 4), line.Trim());
        Assert.StartsWith("    ", line.PadRight(8));
    }

    /// <summary>Larger boxes give larger previews, so they scale with the window.</summary>
    [Fact]
    public void CanScaleWithBox()
    {
        // Arrange
        DecodedImage image = Create(800, 600, (x, y) => ((byte)(x % 256), (byte)(y % 256), 0, 255));

        // Act
        IReadOnlyList<string> small = AsciiArt.Render(image, 40, 10);
        IReadOnlyList<string> large = AsciiArt.Render(image, 160, 40);

        // Assert
        Assert.True(large.Count > small.Count * 3);
        Assert.True(large.Max(l => l.Length) > small.Max(l => l.Length) * 3);
    }

    /// <summary>Shade blocks keep the extremes and dither a gradient into all five levels.</summary>
    [Fact]
    public void CanRenderShadeBlocks()
    {
        // Arrange: horizontal gradient from black to white
        DecodedImage image = Create(200, 40, (x, _) => ((byte)(x * 255 / 199), (byte)(x * 255 / 199), (byte)(x * 255 / 199), 255));

        // Act
        IReadOnlyList<string> lines = AsciiArt.Render(image, 100, 10);

        // Assert: left blank, right full, and every shade appears
        string row = lines[5].PadRight(100);
        Assert.Equal(' ', row[0]);
        Assert.Equal('█', row[^1]);
        Assert.All(AsciiArt.BlockRamp, shade => Assert.Contains(shade, string.Concat(lines)));
    }
}
