namespace Uniform.Matrix.Video.Vp8;

using System.Buffers.Binary;
using System.Runtime.Intrinsics;

// Intra and inter prediction, and adding the residual, RFC 6386 sections 12, 14 and 18
public sealed partial class Vp8Decoder
{
    /// <summary>Six-tap subpixel filters of version 0, by eighth-pixel position.</summary>
    private static readonly short[] SixTapFilters =
    [
        0, 0, 128, 0, 0, 0,
        0, -6, 123, 12, -1, 0,
        2, -11, 108, 36, -8, 1,
        0, -9, 93, 50, -6, 0,
        3, -16, 77, 77, -16, 3,
        0, -6, 50, 93, -9, 0,
        1, -8, 36, 108, -11, 2,
        0, -1, 12, 123, -6, 0,
    ];

    /// <summary>Bilinear subpixel filters of versions 1 to 3, as six taps.</summary>
    private static readonly short[] BilinearFilters =
    [
        0, 0, 128, 0, 0, 0,
        0, 0, 112, 16, 0, 0,
        0, 0, 96, 32, 0, 0,
        0, 0, 80, 48, 0, 0,
        0, 0, 64, 64, 0, 0,
        0, 0, 48, 80, 0, 0,
        0, 0, 32, 96, 0, 0,
        0, 0, 16, 112, 0, 0,
    ];

    private readonly byte[] filterTemp = new byte[16 * (16 + 5)];
    private byte[] edgeBlock = [];

    /// <summary>Predict macroblock row <paramref name="row"/> into the current frame and add its residual.</summary>
    private void Predict(int row)
    {
        // Planes at the row's first macroblock
        Vp8FrameBuffer frame = references[Vp8Modes.CurrentFrame]!;
        int stride = frame.Stride, uvStride = frame.UvStride;
        int y = frame.YOrigin + row * 16 * stride, u = frame.UvOrigin + row * 8 * uvStride, v = u;
        if (edgeBlock.Length < 9 * stride) edgeBlock = new byte[9 * stride];

        // Out-of-frame pixels left of the row, and the corner of the first row
        ref MacroblockInfo first = ref macroblocks[MacroblockIndex(row, 0)];
        FixupLeft(frame.Y, y, 16, stride, row, first.YMode);
        FixupLeft(frame.U, u, 8, uvStride, row, first.UvMode);
        FixupLeft(frame.V, v, 8, uvStride, row, first.UvMode);
        if (row == 0) frame.Y[y - stride - 1] = 127;

        for (int column = 0; column < columns; column++)
        {
            // Out-of-frame pixels above the first row
            int index = MacroblockIndex(row, column);
            ref MacroblockInfo mb = ref macroblocks[index];
            if (row == 0)
            {
                FixupAbove(frame.Y, y, 16, stride, column, mb.YMode);
                FixupAbove(frame.U, u, 8, uvStride, column, mb.UvMode);
                FixupAbove(frame.V, v, 8, uvStride, column, mb.UvMode);
            }

            // Intra or inter, with the second-order DC of whole-macroblock modes
            int blocks = column * CoefficientsPerMacroblock;
            if (mb.YMode <= Vp8Modes.Subblocks)
            {
                PredictIntraLuma(frame.Y, y, stride, index, mb.YMode, blocks);
                PredictIntraChroma(frame.U, frame.V, u, v, uvStride, mb.UvMode, blocks);
            }
            else
            {
                if (mb.YMode != Vp8Modes.Split) Vp8Transform.InverseWalsh(coefficients, blocks + 24 * 16, blocks);
                PredictInter(frame, y, u, column * 16, row * 16, index, ref mb, blocks);
            }
            y += 16;
            u += 8;
            v += 8;
        }

        // Four pixels past the last macroblock's bottom row, for the next row's above-right
        byte last = frame.Y[y - 1 + 15 * stride];
        // Done
        for (int i = 0; i < 4; i++) frame.Y[y + 15 * stride + i] = last;
    }

    /// <summary>Set the column left of a row's first macroblock: 129, or for DC prediction below the first row the above row, so that DC averages the above pixels only.</summary>
    private static void FixupLeft(byte[] plane, int predict, int size, int stride, int row, int mode)
    {
        // DC copies the above row down the left column
        if (mode == Vp8Modes.Dc && row != 0)
        {
            for (int i = 0; i < size; i++) plane[predict - 1 + i * stride] = plane[predict - stride + i];
            return;
        }
        // Done: 129, the corner too
        for (int i = -1; i < size; i++) plane[predict - 1 + i * stride] = 129;
    }

    /// <summary>Set the row above a first-row macroblock: 127, or for DC prediction past the first column the left column, so that DC averages the left pixels only; and four 127s above-right.</summary>
    private static void FixupAbove(byte[] plane, int predict, int size, int stride, int column, int mode)
    {
        // DC copies the left column into the above row
        int above = predict - stride;
        if (mode == Vp8Modes.Dc && column != 0)
        {
            for (int i = 0; i < size; i++) plane[above + i] = plane[predict - 1 + i * stride];
        }
        else plane.AsSpan(above - 1, size + 1).Fill(127);
        // Done
        plane.AsSpan(above + size, 4).Fill(127);
    }

    /// <summary>Predict an intra macroblock's luma, per subblock or whole, and add the residual.</summary>
    private void PredictIntraLuma(byte[] plane, int predict, int stride, int index, int mode, int blocks)
    {
        // Subblocks predict from those before, with the residual added after each
        if (mode == Vp8Modes.Subblocks)
        {
            CopyDown(plane, predict, stride);
            for (int i = 0; i < 16; i++)
            {
                int block = predict + (i >> 2) * 4 * stride + (i & 3) * 4;
                PredictSubblock(plane, block, stride, splitModes[index * 16 + i]);
                Vp8Transform.InverseDctAdd(coefficients, blocks + i * 16, plane, block, plane, block, stride);
            }
            return;
        }

        // Whole macroblock, its blocks' DC from the second-order block
        PredictWhole(plane, predict, stride, 16, mode);
        Vp8Transform.InverseWalsh(coefficients, blocks + 24 * 16, blocks);
        // Done
        for (int i = 0; i < 16; i++)
        {
            int block = predict + (i >> 2) * 4 * stride + (i & 3) * 4;
            Vp8Transform.InverseDctAdd(coefficients, blocks + i * 16, plane, block, plane, block, stride);
        }
    }

    /// <summary>Predict an intra macroblock's chroma, both planes with the same mode, and add the residual.</summary>
    private void PredictIntraChroma(byte[] uPlane, byte[] vPlane, int u, int v, int stride, int mode, int blocks)
    {
        // Prediction, then blocks 16 to 19 of U and 20 to 23 of V
        PredictWhole(uPlane, u, stride, 8, mode);
        PredictWhole(vPlane, v, stride, 8, mode);
        // Done
        for (int i = 0; i < 4; i++)
        {
            int offset = (i >> 1) * 4 * stride + (i & 1) * 4;
            Vp8Transform.InverseDctAdd(coefficients, blocks + (16 + i) * 16, uPlane, u + offset, uPlane, u + offset, stride);
            Vp8Transform.InverseDctAdd(coefficients, blocks + (20 + i) * 16, vPlane, v + offset, vPlane, v + offset, stride);
        }
    }

    /// <summary>Copy the four pixels above-right of subblock 3 to above-right of subblocks 7, 11 and 15, which have none of their own yet.</summary>
    private static void CopyDown(byte[] plane, int predict, int stride)
    {
        // From the above row, three times down
        int source = predict + 16 - stride;
        // Done
        for (int row = 3; row < 16; row += 4) Array.Copy(plane, source, plane, predict + 16 + row * stride, 4);
    }

    /// <summary>Predict a <paramref name="size"/> square block with a whole-block mode: DC, vertical, horizontal or TrueMotion.</summary>
    private static void PredictWhole(byte[] plane, int predict, int stride, int size, int mode)
    {
        // By mode, from the above row and the left column
        int above = predict - stride;
        switch (mode)
        {
            case Vp8Modes.Vertical:
                for (int i = 0; i < size; i++) Array.Copy(plane, above, plane, predict + i * stride, size);
                break;
            case Vp8Modes.Horizontal:
                for (int i = 0; i < size; i++) plane.AsSpan(predict + i * stride, size).Fill(plane[predict - 1 + i * stride]);
                break;
            case Vp8Modes.TrueMotion:
                PredictTrueMotion(plane, predict, stride, size);
                break;
            default:
                PredictDc(plane, predict, stride, size);
                break;
        }
    }

    /// <summary>Fill a <paramref name="size"/> square block with the average of its above row and left column.</summary>
    private static void PredictDc(byte[] plane, int predict, int stride, int size)
    {
        // Sum, then the rounded average
        int sum = 0;
        for (int i = 0; i < size; i++) sum += plane[predict - 1 + i * stride] + plane[predict - stride + i];
        int shift = size == 16 ? 5 : size == 8 ? 4 : 3;
        byte dc = (byte)((sum + (1 << (shift - 1))) >> shift);
        // Done
        for (int i = 0; i < size; i++) plane.AsSpan(predict + i * stride, size).Fill(dc);
    }

    /// <summary>Fill a <paramref name="size"/> square block with left plus above minus above-left, clamped.</summary>
    private static void PredictTrueMotion(byte[] plane, int predict, int stride, int size)
    {
        // Per pixel
        int above = predict - stride, corner = plane[above - 1];
        for (int row = 0; row < size; row++)
        {
            int left = plane[predict - 1 + row * stride];
            for (int i = 0; i < size; i++) plane[predict + row * stride + i] = Vp8Transform.Clamp(left + plane[above + i] - corner);
        }
    }

    /// <summary>Predict a 4x4 subblock with one of the ten subblock modes, from the above row with four pixels above-right, the left column, and the corner.</summary>
    private static void PredictSubblock(byte[] p, int b, int stride, int mode)
    {
        // Edge pixels: above A0 to A7, corner, left L0 to L3
        int a = b - stride;
        int A0 = p[a], A1 = p[a + 1], A2 = p[a + 2], A3 = p[a + 3], A4 = p[a + 4], A5 = p[a + 5], A6 = p[a + 6], A7 = p[a + 7];
        int P = p[a - 1];
        int L0 = p[b - 1], L1 = p[b - 1 + stride], L2 = p[b - 1 + 2 * stride], L3 = p[b - 1 + 3 * stride];
        int r1 = b + stride, r2 = b + 2 * stride, r3 = b + 3 * stride;

        switch (mode)
        {
            case Vp8Modes.BlockDc:
                PredictDc(p, b, stride, 4);
                return;
            case Vp8Modes.BlockTrueMotion:
                PredictTrueMotion(p, b, stride, 4);
                return;
            case Vp8Modes.BlockVertical:
                {
                    byte c0 = Avg3(P, A0, A1), c1 = Avg3(A0, A1, A2), c2 = Avg3(A1, A2, A3), c3 = Avg3(A2, A3, A4);
                    for (int row = 0; row < 4; row++) Row(p, b + row * stride, c0, c1, c2, c3);
                    return;
                }
            case Vp8Modes.BlockHorizontal:
                p.AsSpan(b, 4).Fill(Avg3(P, L0, L1));
                p.AsSpan(r1, 4).Fill(Avg3(L0, L1, L2));
                p.AsSpan(r2, 4).Fill(Avg3(L1, L2, L3));
                p.AsSpan(r3, 4).Fill(Avg3(L2, L3, L3));
                return;
            case Vp8Modes.BlockLeftDown:
                {
                    byte d0 = Avg3(A0, A1, A2), d1 = Avg3(A1, A2, A3), d2 = Avg3(A2, A3, A4), d3 = Avg3(A3, A4, A5), d4 = Avg3(A4, A5, A6), d5 = Avg3(A5, A6, A7), d6 = Avg3(A6, A7, A7);
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d1, d2, d3, d4);
                    Row(p, r2, d2, d3, d4, d5);
                    Row(p, r3, d3, d4, d5, d6);
                    return;
                }
            case Vp8Modes.BlockRightDown:
                {
                    byte d0 = Avg3(L0, P, A0), d1 = Avg3(P, A0, A1), d2 = Avg3(A0, A1, A2), d3 = Avg3(A1, A2, A3), d4 = Avg3(L1, L0, P), d5 = Avg3(L2, L1, L0), d6 = Avg3(L3, L2, L1);
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d4, d0, d1, d2);
                    Row(p, r2, d5, d4, d0, d1);
                    Row(p, r3, d6, d5, d4, d0);
                    return;
                }
            case Vp8Modes.BlockVerticalRight:
                {
                    byte d0 = Avg2(P, A0), d1 = Avg2(A0, A1), d2 = Avg2(A1, A2), d3 = Avg2(A2, A3);
                    byte d4 = Avg3(L0, P, A0), d5 = Avg3(P, A0, A1), d6 = Avg3(A0, A1, A2), d7 = Avg3(A1, A2, A3);
                    byte d8 = Avg3(L1, L0, P), d9 = Avg3(L2, L1, L0);
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d4, d5, d6, d7);
                    Row(p, r2, d8, d0, d1, d2);
                    Row(p, r3, d9, d4, d5, d6);
                    return;
                }
            case Vp8Modes.BlockVerticalLeft:
                {
                    byte d0 = Avg2(A0, A1), d1 = Avg2(A1, A2), d2 = Avg2(A2, A3), d3 = Avg2(A3, A4);
                    byte d4 = Avg3(A0, A1, A2), d5 = Avg3(A1, A2, A3), d6 = Avg3(A2, A3, A4), d7 = Avg3(A3, A4, A5);
                    byte d8 = Avg3(A4, A5, A6), d9 = Avg3(A5, A6, A7);
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d4, d5, d6, d7);
                    Row(p, r2, d1, d2, d3, d8);
                    Row(p, r3, d5, d6, d7, d9);
                    return;
                }
            case Vp8Modes.BlockHorizontalDown:
                {
                    byte d0 = Avg2(L0, P), d1 = Avg3(L0, P, A0), d2 = Avg3(P, A0, A1), d3 = Avg3(A0, A1, A2);
                    byte d4 = Avg2(L1, L0), d5 = Avg3(L1, L0, P), d6 = Avg2(L2, L1), d7 = Avg3(L2, L1, L0);
                    byte d8 = Avg2(L3, L2), d9 = Avg3(L3, L2, L1);
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d4, d5, d0, d1);
                    Row(p, r2, d6, d7, d4, d5);
                    Row(p, r3, d8, d9, d6, d7);
                    return;
                }
            default:
                {
                    // Horizontal and up
                    byte d0 = Avg2(L0, L1), d1 = Avg3(L0, L1, L2), d2 = Avg2(L1, L2), d3 = Avg3(L1, L2, L3);
                    byte d4 = Avg2(L2, L3), d5 = Avg3(L2, L3, L3), d6 = (byte)L3;
                    Row(p, b, d0, d1, d2, d3);
                    Row(p, r1, d2, d3, d4, d5);
                    Row(p, r2, d4, d5, d6, d6);
                    Row(p, r3, d6, d6, d6, d6);
                    return;
                }
        }

        // Rounded averages and a row of four
        static byte Avg2(int x, int y) => (byte)((x + y + 1) >> 1);
        static byte Avg3(int x, int y, int z) => (byte)((x + 2 * y + z + 2) >> 2);
        static void Row(byte[] plane, int at, byte c0, byte c1, byte c2, byte c3)
        {
            plane[at] = c0;
            plane[at + 1] = c1;
            plane[at + 2] = c2;
            plane[at + 3] = c3;
        }
    }

    /// <summary>Predict an inter macroblock from its reference frame, per 4x4 block, and add the residual; blocks near the edge read from an extended copy.</summary>
    private void PredictInter(Vp8FrameBuffer frame, int y, int u, int x0, int y0, int index, ref MacroblockInfo mb, int blocks)
    {
        // Reference, and the chroma vectors: halved, or averaged per 8x8 of a split macroblock
        Vp8FrameBuffer reference = references[mb.RefFrame] ?? throw new InvalidDataException("VP8 frame refers to a missing reference frame.");
        bool fullPixel = version == 3;
        Span<MotionVector> chroma = stackalloc MotionVector[4];
        if (mb.YMode != Vp8Modes.Split)
        {
            int cx = (mb.Mv.X + 1 + (mb.Mv.X >> 31) * 2) / 2, cy = (mb.Mv.Y + 1 + (mb.Mv.Y >> 31) * 2) / 2;
            if (fullPixel)
            {
                cx &= ~7;
                cy &= ~7;
            }
            chroma.Fill(new MotionVector(cx, cy));
        }
        else
        {
            chroma[0] = SplitChromaMv(index, 0, fullPixel);
            chroma[1] = SplitChromaMv(index, 2, fullPixel);
            chroma[2] = SplitChromaMv(index, 8, fullPixel);
            chroma[3] = SplitChromaMv(index, 10, fullPixel);
        }

        // Luma blocks
        int stride = frame.Stride, uvStride = frame.UvStride;
        int width = columns * 16, height = rows * 16;
        for (int b = 0; b < 16; b++)
        {
            MotionVector mv = mb.YMode == Vp8Modes.Split ? splitMvs[index * 16 + b] : mb.Mv;
            int at = y + (b >> 2) * 4 * stride + (b & 3) * 4;
            ReconstructBlock(frame.Y, reference.Y, reference.YOrigin, at, stride, mv, mb.NeedsBorder, x0 + (b & 3) * 4, y0 + (b >> 2) * 4, width, height, blocks + b * 16);
        }

        // Chroma blocks of both planes
        for (int b = 0; b < 4; b++)
        {
            int at = u + (b >> 1) * 4 * uvStride + (b & 1) * 4;
            int cx = x0 / 2 + (b & 1) * 4, cy = y0 / 2 + (b >> 1) * 4;
            ReconstructBlock(frame.U, reference.U, reference.UvOrigin, at, uvStride, chroma[b], mb.NeedsBorder, cx, cy, width / 2, height / 2, blocks + (16 + b) * 16);
            ReconstructBlock(frame.V, reference.V, reference.UvOrigin, at, uvStride, chroma[b], mb.NeedsBorder, cx, cy, width / 2, height / 2, blocks + (20 + b) * 16);
        }
    }

    /// <summary>Return the chroma vector of the 8x8 luma area at subblock <paramref name="b"/>: the rounded average of its four vectors.</summary>
    private MotionVector SplitChromaMv(int index, int b, bool fullPixel)
    {
        // Sums, rounded away from zero, in eighths of chroma
        int at = index * 16 + b;
        int x = splitMvs[at].X + splitMvs[at + 1].X + splitMvs[at + 4].X + splitMvs[at + 5].X;
        int y = splitMvs[at].Y + splitMvs[at + 1].Y + splitMvs[at + 4].Y + splitMvs[at + 5].Y;
        x = (x < 0 ? x - 4 : x + 4) / 8;
        y = (y < 0 ? y - 4 : y + 4) / 8;
        if (fullPixel)
        {
            x &= ~7;
            y &= ~7;
        }
        // Return vector
        return new MotionVector(x, y);
    }

    /// <summary>Predict a 4x4 block at <paramref name="output"/> from the reference moved by <paramref name="mv"/>, filtered for subpixel positions, and add its residual; near the edge, from a copy with the edge pixels repeated.</summary>
    private void ReconstructBlock(byte[] plane, byte[] referencePlane, int origin, int output, int stride, MotionVector mv, bool needsBorder, int x, int y, int width, int height, int block)
    {
        // Source in the reference, or in the extended copy when the filter would read past the edge
        int px = x + (mv.X >> 3), py = y + (mv.Y >> 3);
        byte[] source = referencePlane;
        int at = output + (mv.Y >> 3) * stride + (mv.X >> 3);
        if (needsBorder && (px < 2 || px + 4 - 1 + 3 >= width || py < 2 || py + 4 - 1 + 3 >= height))
        {
            BuildEdgeBlock(referencePlane, origin, stride, px - 2, py - 2, width, height);
            source = edgeBlock;
            at = 2 * stride + 2;
        }

        // Whole pixels add to the reference directly; others are filtered first
        int mx = mv.X & 7, my = mv.Y & 7;
        if ((mx | my) == 0)
        {
            Vp8Transform.InverseDctAdd(coefficients, block, source, at, plane, output, stride);
            return;
        }
        SixTap(plane, output, source, at, stride, mx, my);
        // Done
        Vp8Transform.InverseDctAdd(coefficients, block, plane, output, plane, output, stride);
    }

    /// <summary>The six-tap filter of <see cref="SixTap"/> with vectors: each row of 4 pixels is one vector, summed tap by tap from the six shifted rows, rounded, shifted and clamped as the scalar filter does.</summary>
    private void SixTapVector(byte[] plane, int output, byte[] source, int at, int stride, short[] filters, int h, int v)
    {
        // Horizontal pass: rows two above to three below, at offsets -2 to +3
        Vector128<int> h0 = Vector128.Create((int)filters[h]), h1 = Vector128.Create((int)filters[h + 1]), h2 = Vector128.Create((int)filters[h + 2]);
        Vector128<int> h3 = Vector128.Create((int)filters[h + 3]), h4 = Vector128.Create((int)filters[h + 4]), h5 = Vector128.Create((int)filters[h + 5]);
        Vector128<int> round = Vector128.Create(64), zero = Vector128<int>.Zero, max = Vector128.Create(255);
        int start = at - 2 * stride;
        for (int row = 0; row < 4 + 5; row++)
        {
            int s = start + row * stride;
            Vector128<int> sum = Load(source, s - 2) * h0 + Load(source, s - 1) * h1 + Load(source, s) * h2 + Load(source, s + 1) * h3 + Load(source, s + 2) * h4 + Load(source, s + 3) * h5 + round;
            Store(filterTemp, row * 16, Vector128.Min(Vector128.Max(sum >> 7, zero), max));
        }

        // Vertical pass into the output, from rows of the horizontal one
        Vector128<int> v0 = Vector128.Create((int)filters[v]), v1 = Vector128.Create((int)filters[v + 1]), v2 = Vector128.Create((int)filters[v + 2]);
        Vector128<int> v3 = Vector128.Create((int)filters[v + 3]), v4 = Vector128.Create((int)filters[v + 4]), v5 = Vector128.Create((int)filters[v + 5]);
        for (int row = 0; row < 4; row++)
        {
            int i = (row + 2) * 16;
            Vector128<int> sum = Load(filterTemp, i - 32) * v0 + Load(filterTemp, i - 16) * v1 + Load(filterTemp, i) * v2 + Load(filterTemp, i + 16) * v3 + Load(filterTemp, i + 32) * v4 + Load(filterTemp, i + 48) * v5 + round;
            // Done per row
            Store(plane, output + row * stride, Vector128.Min(Vector128.Max(sum >> 7, zero), max));
        }
    }

    /// <summary>Return the 4 bytes at <paramref name="index"/> widened to 32-bit lanes.</summary>
    private static Vector128<int> Load(byte[] bytes, int index)
    {
        // Four bytes as one scalar, then widened twice
        uint four = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
        // Return lanes
        return Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(four).AsByte())).AsInt32();
    }

    /// <summary>Write 4 lanes of 0 to 255 as bytes at <paramref name="index"/>.</summary>
    private static void Store(byte[] bytes, int index, Vector128<int> values)
    {
        // Narrowed twice, the lowest four bytes written
        Vector128<byte> narrowed = Vector128.Narrow(Vector128.Narrow(values.AsUInt32(), values.AsUInt32()), Vector128<ushort>.Zero);
        // Done
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index, 4), narrowed.AsUInt32().ToScalar());
    }

    /// <summary>Copy the 9x9 pixels at (<paramref name="x"/>, <paramref name="y"/>) of a plane into <see cref="edgeBlock"/>, repeating the edge pixels for those outside the <paramref name="width"/> by <paramref name="height"/> frame.</summary>
    private void BuildEdgeBlock(byte[] plane, int origin, int stride, int x, int y, int width, int height)
    {
        // The row of the frame each output row repeats
        const int Size = 9;
        int row = origin + (y >= height ? (height - 1) * stride : y > 0 ? y * stride : 0);
        int destination = 0;
        for (int i = 0; i < Size; i++)
        {
            // Left fill, the copied middle, right fill
            int left = Math.Min(x < 0 ? -x : 0, Size);
            int right = Math.Min(x + Size > width ? x + Size - width : 0, Size);
            int copy = Size - left - right;
            if (left > 0) edgeBlock.AsSpan(destination, left).Fill(plane[row]);
            if (copy > 0) Array.Copy(plane, row + x + left, edgeBlock, destination + left, copy);
            if (right > 0) edgeBlock.AsSpan(destination + left + Math.Max(copy, 0), right).Fill(plane[row + width - 1]);
            destination += stride;
            y++;
            if (y < height && y > 0) row += stride;
        }
    }

    /// <summary>Filter a 4x4 block from <paramref name="source"/> at the subpixel position (<paramref name="mx"/>, <paramref name="my"/>) eighths into <paramref name="output"/>: rows first, then columns, each pass rounded and clamped.</summary>
    private void SixTap(byte[] plane, int output, byte[] source, int at, int stride, int mx, int my)
    {
        // Vectors where the hardware has them: a row of 4 is one vector of 32-bit sums
        short[] filters = version == 0 ? SixTapFilters : BilinearFilters;
        int h = mx * 6, v = my * 6;
        if (Vector128.IsHardwareAccelerated)
        {
            SixTapVector(plane, output, source, at, stride, filters, h, v);
            return;
        }

        // Horizontal pass over the rows the vertical one needs, two above and three below
        int start = at - 2 * stride;
        for (int row = 0; row < 4 + 5; row++)
        {
            int s = start + row * stride;
            for (int c = 0; c < 4; c++)
            {
                int i = s + c;
                int sum = source[i - 2] * filters[h] + source[i - 1] * filters[h + 1] + source[i] * filters[h + 2] + source[i + 1] * filters[h + 3] + source[i + 2] * filters[h + 4] + source[i + 3] * filters[h + 5] + 64;
                filterTemp[row * 16 + c] = Vp8Transform.Clamp(sum >> 7);
            }
        }

        // Vertical pass into the output
        for (int row = 0; row < 4; row++)
        {
            for (int c = 0; c < 4; c++)
            {
                int i = (row + 2) * 16 + c;
                int sum = filterTemp[i - 32] * filters[v] + filterTemp[i - 16] * filters[v + 1] + filterTemp[i] * filters[v + 2] + filterTemp[i + 16] * filters[v + 3] + filterTemp[i + 32] * filters[v + 4] + filterTemp[i + 48] * filters[v + 5] + 64;
                plane[output + row * stride + c] = Vp8Transform.Clamp(sum >> 7);
            }
        }
    }
}
