namespace Uniform.Matrix.Server.Media;

using StbImageSharp;
using StbImageWriteSharp;

/// <summary>Makes <see href="https://spec.matrix.org/latest/client-server-api/#thumbnails">thumbnails</see> of images: one of a few standard sizes, cropped or scaled, never larger than the image.</summary>
public static class Thumbnailer
{
    /// <summary>Thumbnail method that fills the size and crops the rest.</summary>
    public const string Crop = "crop";
    /// <summary>Thumbnail method that fits the image inside the size.</summary>
    public const string Scale = "scale";
    private const int JpegQuality = 80;
    private static readonly (int Width, int Height, string Method)[] Sizes = [(32, 32, Crop), (96, 96, Crop), (320, 240, Scale), (640, 480, Scale), (800, 600, Scale)];
    private static readonly string[] SourceTypes = ["image/jpeg", "image/png", "image/gif", "image/bmp"];

    /// <summary>Return whether thumbnails can be made from <paramref name="contentType"/>.</summary>
    public static bool CanThumbnail(string contentType) => SourceTypes.Contains(contentType.Split(';')[0].Trim().ToLowerInvariant());

    /// <summary>Return the standard size to make for a request: the smallest of the method at least as large, else the largest.</summary>
    public static (int Width, int Height, string Method) ChooseSize(int width, int height, string method)
    {
        // Sizes of the method, smallest first
        (int Width, int Height, string Method)[] candidates = [.. Sizes.Where(s => s.Method == method)];
        if (candidates.Length == 0) candidates = [.. Sizes.Where(s => s.Method == Scale)];
        // Return a size that covers the request
        return candidates.FirstOrDefault(s => s.Width >= width && s.Height >= height, candidates[^1]);
    }

    /// <summary>Make a thumbnail of an image; return null when it cannot be decoded or has more than <paramref name="maxPixels"/> pixels.</summary>
    /// <returns>PNG when the image has transparency, else JPEG.</returns>
    public static (byte[] Data, string ContentType)? Create(byte[] image, int width, int height, string method, long maxPixels)
    {
        // Check the header before decoding
        ImageInfo? info = ImageInfo.FromStream(new MemoryStream(image));
        if (info is not ImageInfo header || header.Width <= 0 || header.Height <= 0 || (long)header.Width * header.Height > maxPixels) return null;
        ImageResult decoded;
        try
        {
            decoded = ImageResult.FromMemory(image, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }

        // Source region and target size, never enlarged
        int sourceWidth = decoded.Width, sourceHeight = decoded.Height;
        (int x, int y, int w, int h, int targetWidth, int targetHeight) = method == Crop ? CropRegion(sourceWidth, sourceHeight, width, height) : ScaleRegion(sourceWidth, sourceHeight, width, height);
        byte[] pixels = Resize(decoded.Data, sourceWidth, x, y, w, h, targetWidth, targetHeight);

        // PNG keeps transparency, JPEG is smaller
        using MemoryStream output = new();
        ImageWriter writer = new();
        bool transparent = HasTransparency(pixels);
        if (transparent) writer.WritePng(pixels, targetWidth, targetHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        else writer.WriteJpg(pixels, targetWidth, targetHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output, JpegQuality);
        // Return encoded thumbnail
        return (output.ToArray(), transparent ? "image/png" : "image/jpeg");
    }

    /// <summary>Return the whole image as the region and a target that fits inside the size.</summary>
    private static (int X, int Y, int W, int H, int TargetWidth, int TargetHeight) ScaleRegion(int sourceWidth, int sourceHeight, int width, int height)
    {
        // Fit, at most the original size
        double factor = Math.Min(1, Math.Min((double)width / sourceWidth, (double)height / sourceHeight));
        // Return region and target
        return (0, 0, sourceWidth, sourceHeight, Math.Max(1, (int)Math.Round(sourceWidth * factor)), Math.Max(1, (int)Math.Round(sourceHeight * factor)));
    }

    /// <summary>Return the centered region that, scaled, fills the size, and that target size.</summary>
    private static (int X, int Y, int W, int H, int TargetWidth, int TargetHeight) CropRegion(int sourceWidth, int sourceHeight, int width, int height)
    {
        // Cover the size, at most the original size
        double factor = Math.Min(1, Math.Max((double)width / sourceWidth, (double)height / sourceHeight));
        int w = Math.Min(sourceWidth, Math.Max(1, (int)Math.Round(width / factor)));
        int h = Math.Min(sourceHeight, Math.Max(1, (int)Math.Round(height / factor)));
        // Return centered region and target
        return ((sourceWidth - w) / 2, (sourceHeight - h) / 2, w, h, Math.Max(1, (int)Math.Round(w * factor)), Math.Max(1, (int)Math.Round(h * factor)));
    }

    /// <summary>Scale a region of an RGBA image with a box filter, weighting colors by alpha so transparent pixels do not darken edges.</summary>
    private static byte[] Resize(byte[] source, int stride, int x, int y, int regionWidth, int regionHeight, int targetWidth, int targetHeight)
    {
        // Average the source box of each target pixel
        byte[] target = new byte[targetWidth * targetHeight * 4];
        for (int ty = 0; ty < targetHeight; ty++)
        {
            int y0 = y + ty * regionHeight / targetHeight;
            int y1 = Math.Max(y0 + 1, y + (ty + 1) * regionHeight / targetHeight);
            for (int tx = 0; tx < targetWidth; tx++)
            {
                int x0 = x + tx * regionWidth / targetWidth;
                int x1 = Math.Max(x0 + 1, x + (tx + 1) * regionWidth / targetWidth);
                long r = 0, g = 0, b = 0, a = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * stride + sx) * 4;
                        int alpha = source[i + 3];
                        r += source[i] * alpha;
                        g += source[i + 1] * alpha;
                        b += source[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }
                int o = (ty * targetWidth + tx) * 4;
                if (a > 0)
                {
                    target[o] = (byte)(r / a);
                    target[o + 1] = (byte)(g / a);
                    target[o + 2] = (byte)(b / a);
                }
                target[o + 3] = (byte)(a / count);
            }
        }
        // Return pixels
        return target;
    }

    /// <summary>Return whether any pixel of an RGBA image is not fully opaque.</summary>
    private static bool HasTransparency(byte[] pixels)
    {
        // Alpha of each pixel
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255) return true;
        }
        // Done; opaque
        return false;
    }
}
