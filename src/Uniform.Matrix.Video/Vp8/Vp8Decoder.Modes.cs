namespace Uniform.Matrix.Video.Vp8;

// Macroblock modes and motion vectors, RFC 6386 sections 11, 16 and 17
public sealed partial class Vp8Decoder
{
    /// <summary>Read the modes of macroblock row <paramref name="row"/> from the first partition.</summary>
    private void DecodeModes(int row)
    {
        // Motion vector bounds in eighth pixels, one macroblock past the frame
        int toLeft = -(1 << 7), toRight = columns << 7, toTop = -((row + 1) << 7), toBottom = (rows - row) << 7;
        for (int column = 0; column < columns; column++)
        {
            // Segment and skip flag; without skipping, every macroblock has coefficients, as in libvpx
            int index = MacroblockIndex(row, column);
            ref MacroblockInfo mb = ref macroblocks[index];
            if (updateSegmentMap) mb.SegmentId = (byte)ReadSegmentId();
            mb.SkipCoefficients = entropy.SkipEnabled && header.Read(entropy.SkipProb) != 0;

            // Keyframes are all intra; inter frames say per macroblock
            if (keyframe)
            {
                if (!updateSegmentMap) mb.SegmentId = 0;
                DecodeKeyframeMode(index);
                continue;
            }
            if (header.Read(entropy.ProbInter) != 0) DecodeMotionVectors(index, toLeft, toRight, toTop, toBottom);
            else DecodeIntraMode(index);
            toLeft -= 16 << 3;
            toRight -= 16 << 3;
        }
    }

    /// <summary>Read a segment by the segment tree's probabilities.</summary>
    private int ReadSegmentId() => header.Read(segmentTreeProbs[0]) != 0 ? 2 + header.Read(segmentTreeProbs[2]) : header.Read(segmentTreeProbs[1]);

    /// <summary>Read a keyframe macroblock's modes, its subblock modes in the context of the above and left ones.</summary>
    private void DecodeKeyframeMode(int index)
    {
        // Luma, then per subblock when split
        int yMode = header.ReadTree(Vp8Modes.KeyframeYModeTree, Vp8Tables.KeyframeYModeProbs);
        if (yMode == Vp8Modes.Subblocks)
        {
            for (int i = 0; i < 16; i++)
            {
                int above = AboveBlockMode(index, i), left = LeftBlockMode(index, i);
                splitModes[index * 16 + i] = (byte)header.ReadTree(Vp8Modes.SubblockModeTree, Vp8Tables.KeyframeSubblockModeProbs.Slice((above * 10 + left) * 9, 9));
            }
        }

        // Chroma; intra has no motion
        ref MacroblockInfo mb = ref macroblocks[index];
        mb.UvMode = (byte)header.ReadTree(Vp8Modes.UvModeTree, Vp8Tables.KeyframeUvModeProbs);
        mb.YMode = (byte)yMode;
        mb.Mv = default;
        // Done
        mb.RefFrame = Vp8Modes.CurrentFrame;
    }

    /// <summary>Read an intra macroblock of an inter frame: like a keyframe's, with the frame's probabilities and no context.</summary>
    private void DecodeIntraMode(int index)
    {
        // Luma, then per subblock when split
        int yMode = header.ReadTree(Vp8Modes.YModeTree, entropy.YModeProbs);
        if (yMode == Vp8Modes.Subblocks)
        {
            for (int i = 0; i < 16; i++) splitModes[index * 16 + i] = (byte)header.ReadTree(Vp8Modes.SubblockModeTree, Vp8Tables.SubblockModeProbs);
        }

        // Chroma; intra has no motion
        ref MacroblockInfo mb = ref macroblocks[index];
        mb.UvMode = (byte)header.ReadTree(Vp8Modes.UvModeTree, entropy.UvModeProbs);
        mb.YMode = (byte)yMode;
        mb.Mv = default;
        // Done
        mb.RefFrame = Vp8Modes.CurrentFrame;
    }

    /// <summary>Return the mode of the subblock above subblock <paramref name="b"/>: of this macroblock, or of the above one, whose whole-block modes count as the equivalent subblock mode.</summary>
    private int AboveBlockMode(int index, int b)
    {
        // Inside, or the above macroblock's bottom row
        if (b >= 4) return splitModes[index * 16 + b - 4];
        int above = index - (columns + 1);
        // Return mode
        return macroblocks[above].YMode switch
        {
            Vp8Modes.Vertical => Vp8Modes.BlockVertical,
            Vp8Modes.Horizontal => Vp8Modes.BlockHorizontal,
            Vp8Modes.TrueMotion => Vp8Modes.BlockTrueMotion,
            Vp8Modes.Subblocks => splitModes[above * 16 + b + 12],
            _ => Vp8Modes.BlockDc,
        };
    }

    /// <summary>Return the mode of the subblock left of subblock <paramref name="b"/>, as <see cref="AboveBlockMode"/> does.</summary>
    private int LeftBlockMode(int index, int b)
    {
        // Inside, or the left macroblock's right column
        if ((b & 3) != 0) return splitModes[index * 16 + b - 1];
        int left = index - 1;
        // Return mode
        return macroblocks[left].YMode switch
        {
            Vp8Modes.Vertical => Vp8Modes.BlockVertical,
            Vp8Modes.Horizontal => Vp8Modes.BlockHorizontal,
            Vp8Modes.TrueMotion => Vp8Modes.BlockTrueMotion,
            Vp8Modes.Subblocks => splitModes[left * 16 + b + 3],
            _ => Vp8Modes.BlockDc,
        };
    }

    /// <summary>Read an inter macroblock: its reference frame, its mode from the neighbours' motion vectors, and its motion vectors, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-16">RFC 6386 16</see>.</summary>
    private void DecodeMotionVectors(int index, int toLeft, int toRight, int toTop, int toBottom)
    {
        // Reference frame, then the near vectors and their counts
        ref MacroblockInfo mb = ref macroblocks[index];
        mb.RefFrame = (byte)(header.Read(entropy.ProbLast) != 0 ? 2 + header.Read(entropy.ProbGolden) : 1);
        Span<MotionVector> near = stackalloc MotionVector[4];
        Span<int> counts = stackalloc int[4];
        FindNearVectors(index, near, counts);
        Span<byte> probs = stackalloc byte[4];
        ReadOnlySpan<byte> countProbs = Vp8Tables.MvCountProbs;
        for (int i = 0; i < 4; i++) probs[i] = countProbs[counts[i] * 4 + i];
        mb.YMode = (byte)header.ReadTree(Vp8Modes.MvRefTree, probs);
        mb.UvMode = mb.YMode;
        mb.NeedsBorder = false;

        // The vector by mode; the edge check uses whole macroblocks
        int x = (-toLeft - 128) >> 3, y = (-toTop - 128) >> 3, frameWidth = columns * 16, frameHeight = rows * 16;
        switch (mb.YMode)
        {
            case Vp8Modes.Nearest:
                mb.Mv = Clamp(near[1], toLeft, toRight, toTop, toBottom);
                break;
            case Vp8Modes.Near:
                mb.Mv = Clamp(near[2], toLeft, toRight, toTop, toBottom);
                break;
            case Vp8Modes.Zero:
                mb.Mv = default;
                return;
            case Vp8Modes.New:
                MotionVector best = Clamp(near[0], toLeft, toRight, toTop, toBottom);
                MotionVector read = ReadMotionVector();
                mb.Mv = new MotionVector(read.X + best.X, read.Y + best.Y);
                break;
            default:
                DecodeSplit(index, Clamp(near[0], toLeft, toRight, toTop, toBottom));
                mb.Mv = splitMvs[index * 16 + 15];
                mb.NeedsBorder = SplitNeedsBorder(index, x, y, frameWidth, frameHeight);
                return;
        }
        // Done
        if (NeedsBorder(mb.Mv, x, y, 16, frameWidth, frameHeight)) mb.NeedsBorder = true;
    }

    /// <summary>Return whether a split macroblock's luma subblocks, or its chroma blocks, read near or past the edge.</summary>
    private bool SplitNeedsBorder(int index, int x, int y, int frameWidth, int frameHeight)
    {
        // Luma subblocks, summing chroma vectors on the way, as the reference does
        Span<int> chromaX = stackalloc int[4], chromaY = stackalloc int[4];
        for (int b = 0; b < 16; b++)
        {
            MotionVector mv = splitMvs[index * 16 + b];
            int c = (b >> 1 & 1) + (b >> 2 & 2);
            chromaX[c] = (short)(chromaX[c] + mv.X);
            chromaY[c] = (short)(chromaY[c] + mv.Y);
            if (NeedsBorder(mv, x + (b & 3) * 4, y + (b & ~3), 4, frameWidth, frameHeight)) return true;
        }

        // Chroma blocks, in luma coordinates
        for (int b = 0; b < 4; b++)
        {
            int cx = (short)(chromaX[b] + 4 + 8 * (chromaX[b] >> 31)) / 4;
            int cy = (short)(chromaY[b] + 4 + 8 * (chromaY[b] >> 31)) / 4;
            if (NeedsBorder(new MotionVector(cx, cy), x + (b & 1) * 8, y + (b >> 1) * 8, 16, frameWidth, frameHeight)) return true;
        }
        // Return none
        return false;
    }

    /// <summary>Return whether a block of <paramref name="size"/> at (<paramref name="left"/>, <paramref name="top"/>) moved by <paramref name="mv"/> reads within the six-tap filter's reach of the edge.</summary>
    private static bool NeedsBorder(MotionVector mv, int left, int top, int size, int frameWidth, int frameHeight)
    {
        // Distances to each edge
        left += mv.X >> 3;
        top += mv.Y >> 3;
        int right = frameWidth - (left + size);
        int bottom = frameHeight - (top + size);
        // Return whether any is too close
        return left >> 1 < 2 || right >> 1 < 3 || top >> 1 < 2 || bottom >> 1 < 3;
    }

    /// <summary>Return <paramref name="mv"/> clamped to the bounds.</summary>
    private static MotionVector Clamp(MotionVector mv, int toLeft, int toRight, int toTop, int toBottom) =>
        new(mv.X < toLeft ? toLeft : mv.X > toRight ? toRight : mv.X, mv.Y < toTop ? toTop : mv.Y > toBottom ? toBottom : mv.Y);

    /// <summary>Find the distinct motion vectors of the above, left and above-left macroblocks, weighted 2, 2 and 1, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-16.3">RFC 6386 16.3</see>: <paramref name="near"/> gets best, nearest and near, <paramref name="counts"/> their weights and the split count.</summary>
    private void FindNearVectors(int index, Span<MotionVector> near, Span<int> counts)
    {
        // Neighbours
        int above = index - (columns + 1), left = index - 1, aboveLeft = above - 1;
        int refFrame = macroblocks[index].RefFrame;
        near.Clear();
        counts.Clear();
        int mv = 0, count = 0;

        // Above
        ref MacroblockInfo a = ref macroblocks[above];
        if (a.RefFrame != Vp8Modes.CurrentFrame)
        {
            if (!a.Mv.IsZero)
            {
                near[++mv] = Biased(a, refFrame);
                ++count;
            }
            counts[count] += 2;
        }

        // Left
        ref MacroblockInfo l = ref macroblocks[left];
        if (l.RefFrame != Vp8Modes.CurrentFrame)
        {
            if (!l.Mv.IsZero)
            {
                MotionVector biased = Biased(l, refFrame);
                if (!biased.Equals(near[mv]))
                {
                    near[++mv] = biased;
                    ++count;
                }
                counts[count] += 2;
            }
            else counts[0] += 2;
        }

        // Above-left
        ref MacroblockInfo al = ref macroblocks[aboveLeft];
        if (al.RefFrame != Vp8Modes.CurrentFrame)
        {
            if (!al.Mv.IsZero)
            {
                MotionVector biased = Biased(al, refFrame);
                if (!biased.Equals(near[mv]))
                {
                    near[++mv] = biased;
                    ++count;
                }
                counts[count] += 1;
            }
            else counts[0] += 1;
        }

        // Three distinct vectors: above-left may merge with nearest
        if (counts[3] != 0 && near[mv].Equals(near[1])) counts[1] += 1;
        counts[3] = ((a.YMode == Vp8Modes.Split ? 1 : 0) + (l.YMode == Vp8Modes.Split ? 1 : 0)) * 2 + (al.YMode == Vp8Modes.Split ? 1 : 0);

        // Nearest is the more common, and best the nearest when it counts at least as much as zero
        if (counts[2] > counts[1])
        {
            (counts[1], counts[2]) = (counts[2], counts[1]);
            (near[1], near[2]) = (near[2], near[1]);
        }
        // Done
        if (counts[1] >= counts[0]) near[0] = near[1];
    }

    /// <summary>Return the motion vector of <paramref name="neighbour"/>, inverted when its reference frame's sign bias differs from <paramref name="refFrame"/>'s.</summary>
    private MotionVector Biased(in MacroblockInfo neighbour, int refFrame) =>
        (signBias[neighbour.RefFrame] ^ signBias[refFrame]) != 0 ? new MotionVector(-neighbour.Mv.X, -neighbour.Mv.Y) : neighbour.Mv;

    /// <summary>Read a motion vector: rows, then columns.</summary>
    private MotionVector ReadMotionVector()
    {
        // Each component with its probabilities
        int y = ReadMotionComponent(0);
        int x = ReadMotionComponent(19);
        // Return vector
        return new MotionVector(x, y);
    }

    /// <summary>Read one motion vector component with the probabilities at <paramref name="probs"/> in the motion vector probabilities, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-17.2">RFC 6386 17.2</see>; return it in quarter pixels.</summary>
    private int ReadMotionComponent(int probs)
    {
        // Long: bits 0 to 2, 9 to 4, then bit 3, implicit when no higher bit is set; short: a tree
        const int IsShort = 0, Sign = 1, Short = 2, Bits = Short + 8 - 1, LongWidth = 10;
        byte[] p = entropy.MvProbs;
        int x = 0;
        if (header.Read(p[probs + IsShort]) != 0)
        {
            for (int i = 0; i < 3; i++) x += header.Read(p[probs + Bits + i]) << i;
            for (int i = LongWidth - 1; i > 3; i--) x += header.Read(p[probs + Bits + i]) << i;
            if ((x & 0xFFF0) == 0 || header.Read(p[probs + Bits + 3]) != 0) x += 8;
        }
        else x = header.ReadTree(Vp8Modes.SmallMvTree, p.AsSpan(probs + Short, 7));

        // Sign, then to quarter pixels
        if (x != 0 && header.Read(p[probs + Sign]) != 0) x = -x;
        // Return component
        return x << 1;
    }

    /// <summary>Read a split macroblock's partitioning and a motion vector per partition, each relative to the left or above subblock, zero, or new from <paramref name="best"/>.</summary>
    private void DecodeSplit(int index, MotionVector best)
    {
        // Partitioning
        int partitioning = header.ReadTree(Vp8Modes.SplitMvTree, Vp8Tables.SplitMvProbs);
        ReadOnlySpan<byte> partition = Vp8Tables.MvPartitions.Slice(partitioning * 16, 16);

        // Per partition, from its first subblock
        int mask = 0;
        for (int j = 0; mask < 65535; j++)
        {
            int k = 0;
            while (partition[k] != j) k++;
            MotionVector left = LeftBlockMv(index, k), above = AboveBlockMv(index, k);
            MotionVector mv = ReadSubMvRef(left, above) switch
            {
                Vp8Modes.SubLeft => left,
                Vp8Modes.SubAbove => above,
                Vp8Modes.SubZero => default,
                _ => Add(ReadMotionVector(), best),
            };

            // Fill the partition's subblocks
            for (; k < 16; k++)
            {
                if (partition[k] != j) continue;
                splitMvs[index * 16 + k] = mv;
                mask |= 1 << k;
            }
        }

        // Sum of two vectors
        static MotionVector Add(MotionVector a, MotionVector b) => new(a.X + b.X, a.Y + b.Y);
    }

    /// <summary>Read a subblock's motion vector mode, in the context of whether its left and above vectors are zero or equal.</summary>
    private int ReadSubMvRef(MotionVector left, MotionVector above)
    {
        // Context: 0 normal, 1 left zero, 2 above zero, 3 both the same, 4 both zero
        bool leftZero = left.IsZero, aboveZero = above.IsZero, same = left.Equals(above);
        int context = same && leftZero ? 4 : same ? 3 : aboveZero ? 2 : leftZero ? 1 : 0;
        // Return mode
        return header.ReadTree(Vp8Modes.SubMvRefTree, Vp8Tables.SubMvRefProbs.Slice(context * 3, 3));
    }

    /// <summary>Return the motion vector of the subblock left of <paramref name="b"/>.</summary>
    private MotionVector LeftBlockMv(int index, int b)
    {
        // Inside, or the left macroblock's
        if ((b & 3) != 0) return splitMvs[index * 16 + b - 1];
        int left = index - 1;
        // Return vector
        return macroblocks[left].YMode == Vp8Modes.Split ? splitMvs[left * 16 + b + 3] : macroblocks[left].Mv;
    }

    /// <summary>Return the motion vector of the subblock above <paramref name="b"/>.</summary>
    private MotionVector AboveBlockMv(int index, int b)
    {
        // Inside, or the above macroblock's
        if (b >= 4) return splitMvs[index * 16 + b - 4];
        int above = index - (columns + 1);
        // Return vector
        return macroblocks[above].YMode == Vp8Modes.Split ? splitMvs[above * 16 + b + 12] : macroblocks[above].Mv;
    }
}
