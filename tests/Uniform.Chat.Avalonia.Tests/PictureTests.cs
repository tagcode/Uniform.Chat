namespace Uniform.Chat.Avalonia.Tests;

using System.Runtime.InteropServices;
using global::Avalonia;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Media.Imaging;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Testing;

/// <summary>Tests of <see cref="Pictures"/>: photos shown in the chat are scaled and upright.</summary>
public class PictureTests
{
    /// <summary>A phone's photo, stored on its side with an Exif orientation, shows upright, scaled to the width as shown.</summary>
    [AvaloniaFact]
    public void DecodePhotoUpright()
    {
        // Stored 400×200, red left, blue right, to show a quarter turn clockwise
        string path = Path.Combine(Directory.CreateTempSubdirectory("uniform-pictures-").FullName, "photo.jpg");
        File.WriteAllBytes(path, TestPhotos.LeftRedRightBlue(400, 200, orientation: 6));

        // Act
        using Bitmap full = Pictures.Decode(path, 800);
        using Bitmap small = Pictures.Decode(path, 100);

        // Assert: portrait, red on top, blue below; at most 100 wide as shown
        Assert.Equal(new PixelSize(200, 400), full.PixelSize);
        (byte red, _, byte blue) = Pixel(full, 100, 50);
        Assert.True(red > 200 && blue < 60, "red on top");
        (red, _, blue) = Pixel(full, 100, 350);
        Assert.True(blue > 200 && red < 60, "blue below");
        Assert.Equal(new PixelSize(100, 200), small.PixelSize);
    }

    /// <summary>Return the red, green and blue of a pixel, whichever 32-bit order the bitmap has.</summary>
    private static (byte Red, byte Green, byte Blue) Pixel(Bitmap bitmap, int x, int y)
    {
        // One pixel copied out
        byte[] pixel = new byte[4];
        GCHandle handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        bool bgra = bitmap.Format == global::Avalonia.Platform.PixelFormat.Bgra8888;
        // Return channels
        return bgra ? (pixel[2], pixel[1], pixel[0]) : (pixel[0], pixel[1], pixel[2]);
    }
}
