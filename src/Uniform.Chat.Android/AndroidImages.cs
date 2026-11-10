namespace Uniform.Chat.Android;

using Uniform.Matrix.Video;

/// <summary>Converts Android's YUV 4:2:0 images, of the camera or a decoder, to <see cref="VideoFrame"/>s.</summary>
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
}
