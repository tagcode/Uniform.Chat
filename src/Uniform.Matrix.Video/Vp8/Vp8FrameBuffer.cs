namespace Uniform.Matrix.Video.Vp8;

/// <summary>A decoded frame in I420 with a border around each plane, 16 pixels for luma and 8 for chroma, for the out-of-frame pixels intra prediction uses; reference frames share buffers by count.</summary>
internal sealed class Vp8FrameBuffer
{
    /// <summary>Border of the luma plane, in pixels.</summary>
    public const int Border = 16;

    /// <summary>Create a buffer for <paramref name="macroblockColumns"/> by <paramref name="macroblockRows"/> macroblocks.</summary>
    public Vp8FrameBuffer(int macroblockColumns, int macroblockRows)
    {
        // Whole macroblocks plus the borders
        Stride = macroblockColumns * 16 + Border * 2;
        UvStride = Stride / 2;
        int height = macroblockRows * 16 + Border * 2;
        Y = new byte[Stride * height];
        U = new byte[UvStride * (height / 2)];
        V = new byte[UvStride * (height / 2)];
        YOrigin = Border * Stride + Border;
        // Done
        UvOrigin = Border / 2 * UvStride + Border / 2;
    }

    /// <summary>Luma plane with its border.</summary>
    public byte[] Y { get; }
    /// <summary>Blue-difference plane with its border.</summary>
    public byte[] U { get; }
    /// <summary>Red-difference plane with its border.</summary>
    public byte[] V { get; }
    /// <summary>Bytes per luma row.</summary>
    public int Stride { get; }
    /// <summary>Bytes per chroma row.</summary>
    public int UvStride { get; }
    /// <summary>Index of the top-left luma pixel.</summary>
    public int YOrigin { get; }
    /// <summary>Index of the top-left chroma pixel.</summary>
    public int UvOrigin { get; }
    /// <summary>How many reference slots, the current frame's included, use the buffer.</summary>
    public int References { get; set; }
}
