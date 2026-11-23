namespace Uniform.Chat.Avalonia.Services;

using System.Runtime.InteropServices;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using Uniform.Matrix.Hosting.Imaging;

/// <summary>Decodes pictures to show: scaled down while decoding, so large photos take little memory, and upright by a photo's Exif orientation, which Avalonia's decoder does not apply.</summary>
public static class Pictures
{
    /// <summary>Decode the picture at <paramref name="path"/> upright, at most <paramref name="maxWidth"/> pixels wide as shown; <paramref name="knownWidth"/> is its width as shown when known, e.g. from the message.</summary>
    public static Bitmap Decode(string path, int maxWidth, int? knownWidth = null)
    {
        // Its orientation, then scaled while decoding when known to be wide: by height when it turns a quarter
        int orientation;
        using (FileStream head = File.OpenRead(path)) orientation = ImageOrientation.Read(head);
        bool swaps = ImageOrientation.SwapsSides(orientation);
        using FileStream file = File.OpenRead(path);
        if (knownWidth > maxWidth) return Upright(DecodeScaled(file, maxWidth, swaps), orientation);
        Bitmap bitmap = new(file);
        if ((swaps ? bitmap.PixelSize.Height : bitmap.PixelSize.Width) <= maxWidth) return Upright(bitmap, orientation);

        // Wider than told: decode again, scaled
        bitmap.Dispose();
        file.Position = 0;
        // Return scaled and upright
        return Upright(DecodeScaled(file, maxWidth, swaps), orientation);
    }

    /// <summary>Return <paramref name="bitmap"/> turned and mirrored as Exif <paramref name="orientation"/> shows it, disposing the original; itself when it is shown as stored, or when its pixels are not 32-bit.</summary>
    public static Bitmap Upright(Bitmap bitmap, int orientation)
    {
        // Pixels as stored
        if (orientation is < 2 or > 8 || bitmap.Format is not PixelFormat format || format.BitsPerPixel != 32) return bitmap;
        PixelSize size = bitmap.PixelSize;
        byte[] pixels = new byte[size.Width * size.Height * 4];
        GCHandle stored = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), stored.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
        finally { stored.Free(); }

        // Turned, into a new bitmap, which copies them
        (byte[] turned, int width, int height) = ImageOrientation.Apply(pixels, size.Width, size.Height, orientation);
        GCHandle shown = GCHandle.Alloc(turned, GCHandleType.Pinned);
        try
        {
            Bitmap upright = new(format, bitmap.AlphaFormat ?? AlphaFormat.Premul, shown.AddrOfPinnedObject(), new PixelSize(width, height), bitmap.Dpi, width * 4);
            bitmap.Dispose();
            // Return upright
            return upright;
        }
        finally
        {
            shown.Free();
        }
    }

    /// <summary>Decode scaled to <paramref name="maxWidth"/> as shown: the stored height when the picture turns a quarter, else the width.</summary>
    private static Bitmap DecodeScaled(Stream file, int maxWidth, bool swaps) => swaps ? Bitmap.DecodeToHeight(file, maxWidth) : Bitmap.DecodeToWidth(file, maxWidth);
}
