namespace Uniform.Matrix.Testing;

using StbImageWriteSharp;

/// <summary>Photos for tests of turning images upright: red on the left half, blue on the right, as a JPEG with an Exif orientation.</summary>
public static class TestPhotos
{
    /// <summary>Return a JPEG of <paramref name="width"/> × <paramref name="height"/> stored pixels, the left half red and the right half blue, with Exif <paramref name="orientation"/>; with 6, as a phone held upright writes it, it shows red on top.</summary>
    public static byte[] LeftRedRightBlue(int width, int height, int orientation)
    {
        // Two halves
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                if (x < width / 2) pixels[i] = 255;
                else pixels[i + 2] = 255;
            }
        using MemoryStream output = new();
        new ImageWriter().WriteJpg(pixels, width, height, ColorComponents.RedGreenBlue, output, 90);
        // Return it with its orientation
        return ExifJpeg.WithOrientation(output.ToArray(), orientation);
    }
}
