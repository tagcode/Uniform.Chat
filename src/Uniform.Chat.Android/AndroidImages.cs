namespace Uniform.Chat.Android;

using System.Runtime.InteropServices;
using Uniform.Matrix.Video;

/// <summary>Converts Android's YUV 4:2:0 images, of the camera or a decoder, to <see cref="VideoFrame"/>s, and back into an encoder's input images.</summary>
internal static class AndroidImages
{
    /// <summary>Return <paramref name="image"/> as I420, whatever its planes' row and pixel strides.</summary>
    public static VideoFrame ToVideoFrame(global::Android.Media.Image image)
    {
        // Planes by their strides
        global::Android.Media.Image.Plane[] planes = image.GetPlanes()!;
        int width = image.Width, height = image.Height, chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        // Return picture
        return new VideoFrame(width, height, Plane(planes[0], width, height), Plane(planes[1], chromaWidth, chromaHeight), Plane(planes[2], chromaWidth, chromaHeight));
    }

    /// <summary>Return a plane's pixels tightly packed, whatever its row and pixel strides.</summary>
    private static byte[] Plane(global::Android.Media.Image.Plane plane, int width, int height)
    {
        // The buffer's bytes, then each pixel; rows straight when packed
        Java.Nio.ByteBuffer buffer = plane.Buffer!;
        byte[] raw = new byte[buffer.Remaining()];
        buffer.Get(raw);
        int rowStride = plane.RowStride, pixelStride = plane.PixelStride;
        byte[] packed = new byte[width * height];
        for (int row = 0; row < height; row++)
        {
            int from = row * rowStride;
            if (pixelStride == 1 && from + width <= raw.Length)
            {
                Array.Copy(raw, from, packed, row * width, width);
                continue;
            }
            for (int column = 0; column < width && from + column * pixelStride < raw.Length; column++) packed[row * width + column] = raw[from + column * pixelStride];
        }
        // Return pixels
        return packed;
    }

    /// <summary>Write <paramref name="frame"/> into <paramref name="target"/>, an encoder's input image of the same size, whatever its planes' strides: planar, or its chroma interleaved either way round.</summary>
    /// <remarks>The planes are written at their direct buffers' addresses, a row at a time, as single bytes through JNI would be far too slow.</remarks>
    public static void Write(VideoFrame frame, global::Android.Media.Image target)
    {
        // Luma, then chroma: interleaved planes share one buffer a byte apart
        global::Android.Media.Image.Plane[] planes = target.GetPlanes()!;
        WritePlane(planes[0], frame.Y, frame.Width, frame.Height);
        nint u = Address(planes[1]), v = Address(planes[2]);
        if (planes[1].PixelStride == 2 && planes[2].PixelStride == 2 && Math.Abs(u - v) == 1)
        {
            WriteInterleaved(Math.Min(u, v), planes[1].RowStride, u < v ? frame.U : frame.V, u < v ? frame.V : frame.U, frame.ChromaWidth, frame.ChromaHeight);
            return;
        }
        WritePlane(planes[1], frame.U, frame.ChromaWidth, frame.ChromaHeight);
        // Done
        WritePlane(planes[2], frame.V, frame.ChromaWidth, frame.ChromaHeight);
    }

    /// <summary>Return the address of a plane's direct buffer.</summary>
    private static nint Address(global::Android.Media.Image.Plane plane) => global::Android.Runtime.JNIEnv.GetDirectBufferAddress(plane.Buffer!.Handle);

    /// <summary>Write a packed plane of <paramref name="width"/> by <paramref name="height"/> into an image plane, by rows when its pixels are adjacent, else pixel by pixel.</summary>
    private static void WritePlane(global::Android.Media.Image.Plane plane, byte[] pixels, int width, int height)
    {
        // Rows straight when packed
        nint address = Address(plane);
        int rowStride = plane.RowStride, pixelStride = plane.PixelStride;
        for (int row = 0; row < height; row++)
        {
            if (pixelStride == 1)
            {
                Marshal.Copy(pixels, row * width, address + row * rowStride, width);
                continue;
            }
            for (int column = 0; column < width; column++) Marshal.WriteByte(address + row * rowStride + column * pixelStride, pixels[row * width + column]);
        }
    }

    /// <summary>Write two chroma planes interleaved at <paramref name="address"/>, the one at the lower address first in each pair.</summary>
    private static void WriteInterleaved(nint address, int rowStride, byte[] first, byte[] second, int width, int height)
    {
        // Each row's pairs, then the row at once
        byte[] row = new byte[width * 2];
        for (int line = 0; line < height; line++)
        {
            for (int column = 0; column < width; column++)
            {
                row[column * 2] = first[line * width + column];
                row[column * 2 + 1] = second[line * width + column];
            }
            Marshal.Copy(row, 0, address + line * rowStride, row.Length);
        }
    }
}
