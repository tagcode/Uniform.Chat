namespace Uniform.Matrix.Testing;

using System.Buffers.Binary;

/// <summary>JPEG files as phones write them: an Exif segment with an orientation tag, put after the start marker of an encoded JPEG.</summary>
public static class ExifJpeg
{
    /// <summary>Return <paramref name="jpeg"/> with an Exif segment whose first image directory has <paramref name="orientation"/>, in little-endian (<c>II</c>) or big-endian (<c>MM</c>) TIFF order.</summary>
    public static byte[] WithOrientation(byte[] jpeg, int orientation, bool littleEndian = true)
    {
        // TIFF header, one directory with one entry: orientation, type SHORT, count 1, value
        byte[] tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = tiff[1] = (byte)(littleEndian ? 'I' : 'M');
        Write16(tiff.AsSpan(2), 42, littleEndian);
        Write32(tiff.AsSpan(4), 8, littleEndian);
        Write16(tiff.AsSpan(8), 1, littleEndian);
        Write16(tiff.AsSpan(10), 0x0112, littleEndian);
        Write16(tiff.AsSpan(12), 3, littleEndian);
        Write32(tiff.AsSpan(14), 1, littleEndian);
        Write16(tiff.AsSpan(18), (ushort)orientation, littleEndian);

        // APP1 of "Exif\0\0" and the TIFF data, after the start marker
        byte[] segment = [0xFF, 0xE1, 0, 0, .. "Exif\0\0"u8, .. tiff];
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(segment.Length - 2));
        // Return the file
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    /// <summary>Write a 16-bit number in the chosen byte order.</summary>
    private static void Write16(Span<byte> target, ushort value, bool littleEndian)
    {
        // Done
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        else BinaryPrimitives.WriteUInt16BigEndian(target, value);
    }

    /// <summary>Write a 32-bit number in the chosen byte order.</summary>
    private static void Write32(Span<byte> target, uint value, bool littleEndian)
    {
        // Done
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        else BinaryPrimitives.WriteUInt32BigEndian(target, value);
    }
}
