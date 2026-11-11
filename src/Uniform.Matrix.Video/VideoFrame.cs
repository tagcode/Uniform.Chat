namespace Uniform.Matrix.Video;

/// <summary>A decoded picture in I420: full-size luma, and both chroma planes at half the width and height, rounded up; each plane tightly packed.</summary>
public sealed class VideoFrame
{
    /// <summary>Create a picture of <paramref name="width"/> by <paramref name="height"/> pixels from its planes.</summary>
    public VideoFrame(int width, int height, byte[] y, byte[] u, byte[] v)
    {
        // Assert the plane sizes
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (y.Length != width * height) throw new ArgumentException("The luma plane does not match the size.", nameof(y));
        int chroma = (width + 1) / 2 * ((height + 1) / 2);
        if (u.Length != chroma || v.Length != chroma) throw new ArgumentException("A chroma plane does not match the size.");
        Width = width;
        Height = height;
        Y = y;
        U = u;
        // Done
        V = v;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>Width of the chroma planes.</summary>
    public int ChromaWidth => (Width + 1) / 2;
    /// <summary>Height of the chroma planes.</summary>
    public int ChromaHeight => (Height + 1) / 2;
    /// <summary>Luma, <see cref="Width"/> bytes per row.</summary>
    public byte[] Y { get; }
    /// <summary>Blue-difference chroma, <see cref="ChromaWidth"/> bytes per row.</summary>
    public byte[] U { get; }
    /// <summary>Red-difference chroma, <see cref="ChromaWidth"/> bytes per row.</summary>
    public byte[] V { get; }

    /// <summary>Clockwise rotation in degrees that shows the picture upright, 0, 90, 180 or 270, e.g. from a phone held upright, whose camera sees sideways.</summary>
    public int Rotation { get; set; }

    /// <summary>Whether to show the picture mirrored, as a preview of one's own front camera is, like a mirror.</summary>
    public bool Mirrored { get; set; }

    /// <summary>Return the picture turned <paramref name="degrees"/> clockwise, 0, 90, 180 or 270, e.g. by its <see cref="Rotation"/> to send it upright where the receiver is not told the rotation; the result has no rotation of its own.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="degrees"/> is not a quarter turn.</exception>
    public VideoFrame Rotated(int degrees)
    {
        // Assert a quarter turn; none is the picture itself
        degrees = (degrees % 360 + 360) % 360;
        if (degrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees), "Only quarter turns.");
        if (degrees == 0) return this;
        bool sideways = degrees != 180;
        int width = sideways ? Height : Width, height = sideways ? Width : Height;
        // Return each plane turned
        return new VideoFrame(width, height, Turn(Y, Width, Height, degrees), Turn(U, ChromaWidth, ChromaHeight, degrees), Turn(V, ChromaWidth, ChromaHeight, degrees)) { Mirrored = Mirrored };
    }

    /// <summary>Return a plane of <paramref name="width"/> by <paramref name="height"/> turned <paramref name="degrees"/> clockwise.</summary>
    private static byte[] Turn(byte[] plane, int width, int height, int degrees)
    {
        // Each source pixel to its place: clockwise, a row becomes a column from the right
        byte[] turned = new byte[plane.Length];
        for (int row = 0; row < height; row++)
        {
            int from = row * width;
            for (int column = 0; column < width; column++)
            {
                int to = degrees switch
                {
                    90 => column * height + (height - 1 - row),
                    180 => (height - 1 - row) * width + (width - 1 - column),
                    _ => (width - 1 - column) * height + row,
                };
                turned[to] = plane[from + column];
            }
        }
        // Return plane
        return turned;
    }

    /// <summary>Write the picture as 32-bit BGRA, opaque, into <paramref name="destination"/> of <paramref name="stride"/> bytes per row, converting from BT.601 limited range as WebRTC video is.</summary>
    /// <param name="destination">At least <see cref="Height"/> rows.</param>
    /// <param name="stride">Bytes per row, at least 4 × <see cref="Width"/>.</param>
    public void CopyToBgra(Span<byte> destination, int stride)
    {
        // Assert room
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, Width * 4);
        if (destination.Length < stride * (Height - 1) + Width * 4) throw new ArgumentException("The destination is too small.", nameof(destination));

        // Two rows at a time share a chroma row; chroma's part of each color once per pair of pixels
        int chromaWidth = ChromaWidth;
        for (int row = 0; row < Height; row++)
        {
            ReadOnlySpan<byte> luma = Y.AsSpan(row * Width, Width);
            ReadOnlySpan<byte> blue = U.AsSpan(row / 2 * chromaWidth, chromaWidth), red = V.AsSpan(row / 2 * chromaWidth, chromaWidth);
            Span<byte> output = destination.Slice(row * stride, Width * 4);
            for (int x = 0; x < Width; x += 2)
            {
                int d = blue[x / 2] - 128, e = red[x / 2] - 128;
                int r = 409 * e + 128, g = -100 * d - 208 * e + 128, b = 516 * d + 128;
                Pixel(output, x, luma[x], r, g, b);
                if (x + 1 < Width) Pixel(output, x + 1, luma[x + 1], r, g, b);
            }
        }
    }

    /// <summary>Write pixel <paramref name="x"/> of a row from its luma and chroma's parts of red, green and blue, in 1/256ths.</summary>
    private static void Pixel(Span<byte> output, int x, byte luma, int r, int g, int b)
    {
        // Luma scaled from 16..235, then clamped
        int c = 298 * (luma - 16);
        int at = x * 4;
        output[at] = Clamp((c + b) >> 8);
        output[at + 1] = Clamp((c + g) >> 8);
        output[at + 2] = Clamp((c + r) >> 8);
        // Done
        output[at + 3] = 255;
    }

    /// <summary>Return <paramref name="value"/> clamped to a byte.</summary>
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
