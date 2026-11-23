namespace Uniform.Matrix.Hosting.Imaging;

using System.Buffers.Binary;

/// <summary>The <see href="https://www.cipa.jp/std/documents/e/DC-008-2012_E.pdf">Exif</see> orientation of a photo, which phones write instead of turning the pixels, and turning the pixels upright by it.</summary>
/// <remarks>The values 1 to 8 name how the stored pixels are shown: 1 as stored, 3 turned half a turn, 6 a quarter turn clockwise, 8 a quarter turn counterclockwise, and 2, 4, 5 and 7 their mirror images. Only JPEG files carry it here.</remarks>
public static class ImageOrientation
{
    /// <summary>The orientation of pixels shown as stored.</summary>
    public const int Normal = 1;

    /// <summary>How much of a file's start <see cref="Read(Stream)"/> reads, enough for the Exif segment, which is at most 64 KiB, and the small ones before it.</summary>
    private const int HeadBytes = 128 * 1024;

    /// <summary>The Exif tag of the orientation.</summary>
    private const ushort OrientationTag = 0x0112;

    /// <summary>Return the orientation of the image read from the start of <paramref name="stream"/>, 1 to 8; <see cref="Normal"/> for images without one, and for other formats than JPEG.</summary>
    public static int Read(Stream stream)
    {
        // The start of the file, where the Exif segment is
        byte[] head = new byte[HeadBytes];
        int length = 0;
        while (length < head.Length && stream.Read(head, length, head.Length - length) is int read and > 0) length += read;
        // Return the orientation
        return Read(head.AsSpan(0, length));
    }

    /// <summary>Return the orientation of the image in <paramref name="image"/>, its whole file or its start, 1 to 8; <see cref="Normal"/> for images without one, and for other formats than JPEG.</summary>
    public static int Read(ReadOnlySpan<byte> image)
    {
        // A JPEG's segments up to its image data, for the Exif one
        if (image.Length < 4 || image[0] != 0xFF || image[1] != 0xD8) return Normal;
        int at = 2;
        while (at + 4 <= image.Length && image[at] == 0xFF)
        {
            byte marker = image[at + 1];
            if (marker == 0xDA || marker == 0xD9) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(image[(at + 2)..]);
            if (length < 2) break;
            int end = Math.Min(image.Length, at + 2 + length);
            ReadOnlySpan<byte> segment = image[(at + 4)..end];
            if (marker == 0xE1 && segment.StartsWith("Exif\0\0"u8)) return ReadTiff(segment[6..]);
            at += 2 + length;
        }
        // On none
        return Normal;
    }

    /// <summary>Return whether pixels shown in <paramref name="orientation"/> swap width and height, as a quarter turn does.</summary>
    public static bool SwapsSides(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>Return the pixels of an image of 4 bytes per pixel, rows from the top, turned and mirrored as <paramref name="orientation"/> shows them, with the new width and height; the same pixels for <see cref="Normal"/> or an unknown value.</summary>
    /// <param name="pixels">4 bytes per pixel, in any channel order, rows from the top without padding.</param>
    /// <param name="width">Width in pixels as stored.</param>
    /// <param name="height">Height in pixels as stored.</param>
    /// <param name="orientation">Exif orientation, 1 to 8.</param>
    public static (byte[] Pixels, int Width, int Height) Apply(byte[] pixels, int width, int height, int orientation)
    {
        // Nothing to turn
        if (orientation is < 2 or > 8) return (pixels, width, height);
        bool swaps = SwapsSides(orientation);
        int targetWidth = swaps ? height : width;
        int targetHeight = swaps ? width : height;

        // Each stored pixel to its shown place
        byte[] target = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int tx, int ty) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (height - 1 - y, x),
                    7 => (height - 1 - y, width - 1 - x),
                    _ => (y, width - 1 - x),
                };
                Buffer.BlockCopy(pixels, (y * width + x) * 4, target, (ty * targetWidth + tx) * 4, 4);
            }
        }
        // Return turned
        return (target, targetWidth, targetHeight);
    }

    /// <summary>Return the orientation of the first image directory of Exif's TIFF data; <see cref="Normal"/> when it has none.</summary>
    private static int ReadTiff(ReadOnlySpan<byte> tiff)
    {
        // Byte order, then the first directory's entries of 12 bytes: tag, type, count, value
        if (tiff.Length < 8) return Normal;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return Normal;
        uint directory = ReadUInt32(tiff[4..], little);
        if (directory > tiff.Length - 2) return Normal;
        int entries = ReadUInt16(tiff[(int)directory..], little);
        for (int i = 0; i < entries; i++)
        {
            int entry = (int)directory + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (ReadUInt16(tiff[entry..], little) != OrientationTag) continue;
            int value = ReadUInt16(tiff[(entry + 8)..], little);
            return value is >= 1 and <= 8 ? value : Normal;
        }
        // On none
        return Normal;
    }

    /// <summary>Return a 16-bit number in the TIFF data's byte order.</summary>
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool little) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    /// <summary>Return a 32-bit number in the TIFF data's byte order.</summary>
    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
}
