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
}
