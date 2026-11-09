namespace Uniform.Matrix.Video.Vp8;

/// <summary>Prediction modes of VP8 macroblocks and subblocks, as numbered in the coding trees of <see href="https://www.rfc-editor.org/rfc/rfc6386#section-8.1">RFC 6386</see>.</summary>
internal static class Vp8Modes
{
    /// <summary>16x16 intra: the average of the above and left pixels.</summary>
    public const int Dc = 0;
    /// <summary>16x16 intra: the above row repeated down.</summary>
    public const int Vertical = 1;
    /// <summary>16x16 intra: the left column repeated across.</summary>
    public const int Horizontal = 2;
    /// <summary>16x16 intra: TrueMotion, left plus above minus above-left.</summary>
    public const int TrueMotion = 3;
    /// <summary>Intra with a mode per 4x4 subblock.</summary>
    public const int Subblocks = 4;
    /// <summary>Inter: the nearest neighbouring motion vector.</summary>
    public const int Nearest = 5;
    /// <summary>Inter: the next nearest neighbouring motion vector.</summary>
    public const int Near = 6;
    /// <summary>Inter: no motion.</summary>
    public const int Zero = 7;
    /// <summary>Inter: a motion vector of its own, relative to the best one.</summary>
    public const int New = 8;
    /// <summary>Inter: a motion vector per partition of the macroblock.</summary>
    public const int Split = 9;

    /// <summary>Subblock intra: DC.</summary>
    public const int BlockDc = 0;
    /// <summary>Subblock intra: TrueMotion.</summary>
    public const int BlockTrueMotion = 1;
    /// <summary>Subblock intra: vertical, smoothed.</summary>
    public const int BlockVertical = 2;
    /// <summary>Subblock intra: horizontal, smoothed.</summary>
    public const int BlockHorizontal = 3;
    /// <summary>Subblock intra: down and left.</summary>
    public const int BlockLeftDown = 4;
    /// <summary>Subblock intra: down and right.</summary>
    public const int BlockRightDown = 5;
    /// <summary>Subblock intra: vertical and right.</summary>
    public const int BlockVerticalRight = 6;
    /// <summary>Subblock intra: vertical and left.</summary>
    public const int BlockVerticalLeft = 7;
    /// <summary>Subblock intra: horizontal and down.</summary>
    public const int BlockHorizontalDown = 8;
    /// <summary>Subblock intra: horizontal and up.</summary>
    public const int BlockHorizontalUp = 9;
    /// <summary>Subblock inter: the left subblock's motion vector.</summary>
    public const int SubLeft = 10;
    /// <summary>Subblock inter: the above subblock's motion vector.</summary>
    public const int SubAbove = 11;
    /// <summary>Subblock inter: no motion.</summary>
    public const int SubZero = 12;
    /// <summary>Subblock inter: a motion vector of its own.</summary>
    public const int SubNew = 13;

    /// <summary>Reference frame: the frame being decoded, i.e. intra.</summary>
    public const int CurrentFrame = 0;
    /// <summary>Reference frame: the last frame.</summary>
    public const int LastFrame = 1;
    /// <summary>Reference frame: the golden frame.</summary>
    public const int GoldenFrame = 2;
    /// <summary>Reference frame: the alternate reference frame.</summary>
    public const int AltrefFrame = 3;

    /// <summary>Luma mode tree of keyframes.</summary>
    public static ReadOnlySpan<int> KeyframeYModeTree => [-Subblocks, 2, 4, 6, -Dc, -Vertical, -Horizontal, -TrueMotion];
    /// <summary>Luma mode tree of inter frames.</summary>
    public static ReadOnlySpan<int> YModeTree => [-Dc, 2, 4, 6, -Vertical, -Horizontal, -TrueMotion, -Subblocks];
    /// <summary>Chroma mode tree.</summary>
    public static ReadOnlySpan<int> UvModeTree => [-Dc, 2, -Vertical, 4, -Horizontal, -TrueMotion];
    /// <summary>Subblock intra mode tree.</summary>
    public static ReadOnlySpan<int> SubblockModeTree =>
    [
        -BlockDc, 2, -BlockTrueMotion, 4, -BlockVertical, 6, 8, 12, -BlockHorizontal, 10,
        -BlockRightDown, -BlockVerticalRight, -BlockLeftDown, 14, -BlockVerticalLeft, 16, -BlockHorizontalDown, -BlockHorizontalUp,
    ];
    /// <summary>Tree of short motion vector magnitudes, 0 to 7.</summary>
    public static ReadOnlySpan<int> SmallMvTree => [2, 8, 4, 6, -0, -1, -2, -3, 10, 12, -4, -5, -6, -7];
    /// <summary>Inter macroblock mode tree.</summary>
    public static ReadOnlySpan<int> MvRefTree => [-Zero, 2, -Nearest, 4, -Near, 6, -New, -Split];
    /// <summary>Subblock inter mode tree.</summary>
    public static ReadOnlySpan<int> SubMvRefTree => [-SubLeft, 2, -SubAbove, 4, -SubZero, -SubNew];
    /// <summary>Split partitioning tree: 16x8, 8x16, 8x8 or 4x4, numbered 0 to 3.</summary>
    public static ReadOnlySpan<int> SplitMvTree => [-3, 2, -2, 4, -0, -1];
}

/// <summary>A motion vector in quarter pixels of luma, eighths of chroma, as VP8 stores them.</summary>
internal struct MotionVector : IEquatable<MotionVector>
{
    /// <summary>Horizontal part.</summary>
    public short X;
    /// <summary>Vertical part.</summary>
    public short Y;

    /// <summary>Create a vector.</summary>
    public MotionVector(int x, int y)
    {
        // Truncated to 16 bits, as the reference decoder stores them
        X = (short)x;
        Y = (short)y;
    }

    /// <summary>Whether both parts are 0.</summary>
    public readonly bool IsZero => X == 0 && Y == 0;

    /// <summary>Return whether <paramref name="other"/> is the same vector.</summary>
    public readonly bool Equals(MotionVector other) => X == other.X && Y == other.Y;

    /// <summary>Return whether <paramref name="obj"/> is the same vector.</summary>
    public override readonly bool Equals(object? obj) => obj is MotionVector other && Equals(other);

    /// <summary>Return a hash of both parts.</summary>
    public override readonly int GetHashCode() => (ushort)X | Y << 16;
}

/// <summary>What the mode parsing tells of one macroblock, kept between frames as the segment map persists.</summary>
internal struct MacroblockInfo
{
    /// <summary>Luma mode, intra or inter; see <see cref="Vp8Modes"/>.</summary>
    public byte YMode;
    /// <summary>Chroma mode of intra macroblocks.</summary>
    public byte UvMode;
    /// <summary>Segment, 0 to 3.</summary>
    public byte SegmentId;
    /// <summary>Reference frame; <see cref="Vp8Modes.CurrentFrame"/> for intra.</summary>
    public byte RefFrame;
    /// <summary>Whether the macroblock has no coefficients.</summary>
    public bool SkipCoefficients;
    /// <summary>Whether motion compensation reads near or past the frame's edge, which needs the edge extended.</summary>
    public bool NeedsBorder;
    /// <summary>The macroblock's motion vector; of split macroblocks, the last subblock's.</summary>
    public MotionVector Mv;
    /// <summary>Bit per block with coefficients past the DC, and bit 31 when any block has coefficients.</summary>
    public uint EobMask;
}
