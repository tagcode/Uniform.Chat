namespace Durum.Matrix.Client.Terminal.Ui.Images;

using StbImageSharp;
using global::Terminal.Gui.Drawing;

/// <summary>Image decoded to 8-bit RGBA pixels, row by row from the top.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgba">Four bytes per pixel.</param>
public sealed record DecodedImage(int Width, int Height, byte[] Rgba)
{
    /// <summary>Largest dimension accepted, to bound memory for hostile or huge images.</summary>
    public const int MaxDimension = 8192;

    /// <summary>Decode PNG, JPEG, GIF (first frame), BMP, TGA or PSD; return null for other or broken data.</summary>
    public static DecodedImage? TryDecode(byte[] data)
    {
        // Check size from the header before decoding
        ImageInfo? info = ImageInfo.FromStream(new MemoryStream(data));
        if (info is not { Width: > 0 and <= MaxDimension, Height: > 0 and <= MaxDimension }) return null;

        // Decode to RGBA
        try
        {
            ImageResult result = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            return new DecodedImage(result.Width, result.Height, result.Data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Return the pixel at (<paramref name="x"/>, <paramref name="y"/>) as red, green, blue and alpha.</summary>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        // Four bytes per pixel, rows from the top
        int offset = (y * Width + x) * 4;
        // Return channels
        return (Rgba[offset], Rgba[offset + 1], Rgba[offset + 2], Rgba[offset + 3]);
    }

    /// <summary>Return a copy no larger than <paramref name="maxDimension"/> on either side, using nearest-neighbor sampling; return itself when already small enough.</summary>
    public DecodedImage Downscale(int maxDimension)
    {
        // Assert need
        if (Width <= maxDimension && Height <= maxDimension) return this;
        double scale = Math.Min(maxDimension / (double)Width, maxDimension / (double)Height);
        int width = Math.Max(1, (int)(Width * scale));
        int height = Math.Max(1, (int)(Height * scale));

        // Sample source pixels
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Array.Copy(Rgba, ((y * Height / height) * Width + x * Width / width) * 4, rgba, (y * width + x) * 4, 4);
        // Return smaller image
        return new DecodedImage(width, height, rgba);
    }

    /// <summary>Convert to the <c>[x, y]</c> color array of <see cref="global::Terminal.Gui.Views.ImageView"/>.</summary>
    public Color[,] ToColors()
    {
        // Copy pixels
        Color[,] colors = new Color[Width, Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                (byte r, byte g, byte b, byte a) = GetPixel(x, y);
                colors[x, y] = new Color(r, g, b, a);
            }
        // Return colors
        return colors;
    }
}
