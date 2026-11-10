namespace Uniform.Matrix.Video.Vp8;

/// <summary>Encodes pictures as VP8 frames, <see href="https://www.rfc-editor.org/rfc/rfc6386">RFC 6386</see>, in pure C#: keyframes, every macroblock predicted from its edges by DC, and inter frames between them, every macroblock predicted from the previous picture at the same place and skipped when nothing is left to code; one quantizer, the default probabilities and no loop filter.</summary>
/// <remarks>The encoder predicts and reconstructs as a decoder does, with <see cref="Vp8Decoder"/>'s inverse transforms, so its <see cref="Reconstruction"/> is the picture every decoder shows and the reference of the next inter frame. A keyframe comes every <see cref="KeyframeInterval"/> frames, and after <see cref="RequestKeyframe"/>, e.g. when the peer lost packets.</remarks>
public sealed class Vp8Encoder
{
    /// <summary>Probability band of each coefficient position.</summary>
    private static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7];
    /// <summary>Raster position of each coefficient in zigzag order.</summary>
    private static ReadOnlySpan<byte> ZigZag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    /// <summary>Left context entry of each block: 4 luma rows, 2 of U, 2 of V, then Y2.</summary>
    private static ReadOnlySpan<byte> LeftContextIndex => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    /// <summary>Above context entry of each block: 4 luma columns, 2 of U, 2 of V, then Y2.</summary>
    private static ReadOnlySpan<byte> AboveContextIndex => [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];
    /// <summary>Largest token value, of the last category.</summary>
    private const int MaxLevel = 2114;
    private readonly short[] coefficients = new short[25 * 16];
    private readonly int[] levels = new int[25 * 16];
    private int width;
    private int height;
    private int columns;
    private int rows;
    private byte[] y = [];
    private byte[] u = [];
    private byte[] v = [];
    private byte[] referenceY = [];
    private byte[] referenceU = [];
    private byte[] referenceV = [];
    private int sinceKeyframe;
    private bool keyframeRequested = true;

    /// <summary>Create an encoder of quantizer index <paramref name="quantizer"/>, 0 for the finest to 127 for the coarsest.</summary>
    public Vp8Encoder(int quantizer = 40)
    {
        // Assert range
        ArgumentOutOfRangeException.ThrowIfLessThan(quantizer, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantizer, 127);
        // Done
        Quantizer = quantizer;
    }

    /// <summary>The quantizer index of the next frames, 0 to 127: higher makes smaller frames of lower quality.</summary>
    public int Quantizer { get; set; }

    /// <summary>Whether inter frames search for motion: each macroblock may predict from a place moved by an even number of whole pixels, so its chroma moves by whole pixels too. Off predicts every macroblock from the same place.</summary>
    public bool MotionSearch { get; set; } = true;

    /// <summary>Frames from one keyframe to the next; 0 makes every frame a keyframe.</summary>
    public int KeyframeInterval { get; set; } = 60;

    /// <summary>Make the next frame a keyframe.</summary>
    public void RequestKeyframe() => keyframeRequested = true;

    /// <summary>The last frame as decoders show it.</summary>
    public VideoFrame Reconstruction => Crop();

    /// <summary>Encode <paramref name="picture"/>: a keyframe at the start, when asked, after <see cref="KeyframeInterval"/> frames or a change of size; else an inter frame. Return the frame.</summary>
    public byte[] Encode(VideoFrame picture)
    {
        // A keyframe when due
        bool resized = picture.Width != width || picture.Height != height;
        if (resized || keyframeRequested || KeyframeInterval == 0 || sinceKeyframe + 1 >= KeyframeInterval)
        {
            keyframeRequested = false;
            sinceKeyframe = 0;
            return EncodeKeyframe(picture);
        }
        sinceKeyframe++;
        // Return an inter frame
        return EncodeInter(picture);
    }

    /// <summary>Encode <paramref name="picture"/> as a keyframe; return the frame.</summary>
    private byte[] EncodeKeyframe(VideoFrame picture)
    {
        // Planes of whole macroblocks
        Allocate(picture.Width, picture.Height);
        BoolEncoder header = new(), tokens = new();
        int q = Math.Clamp(Quantizer, 0, 127);
        WriteHeader(header, q, keyframe: true);

        // Factors as the decoder computes them: Y AC, U and V DC and AC, Y2 DC and AC
        int yAc = Vp8Tables.AcQuant[q], uvDc = Math.Min((int)Vp8Tables.DcQuant[q], 132), uvAc = Vp8Tables.AcQuant[q];
        int y2Dc = Vp8Tables.DcQuant[q] * 2, y2Ac = Math.Max(Vp8Tables.AcQuant[q] * 155 / 100, 8);

        // Macroblocks in raster order: modes in the first partition, coefficients in the second
        int[] above = new int[columns * 9];
        int[] left = new int[9];
        for (int row = 0; row < rows; row++)
        {
            Array.Clear(left);
            for (int column = 0; column < columns; column++)
            {
                header.WriteTree(Vp8Modes.KeyframeYModeTree, Vp8Tables.KeyframeYModeProbs, Vp8Modes.Dc);
                header.WriteTree(Vp8Modes.UvModeTree, Vp8Tables.KeyframeUvModeProbs, Vp8Modes.Dc);
                Array.Clear(coefficients);
                Array.Clear(levels);
                EncodeLuma(picture, row, column, yAc, y2Dc, y2Ac);
                EncodeChroma(picture.U, u, picture.ChromaWidth, picture.ChromaHeight, row, column, 16, uvDc, uvAc);
                EncodeChroma(picture.V, v, picture.ChromaWidth, picture.ChromaHeight, row, column, 20, uvDc, uvAc);
                WriteTokens(tokens, left, above, column * 9);
            }
        }
        // Return the frame
        return Assemble(header.Finish(), tokens.Finish(), picture.Width, picture.Height, keyframe: true);
    }

    /// <summary>Encode <paramref name="picture"/> as an inter frame from the last reconstruction: each macroblock predicted from the same place or a place found by motion search, and skipped when nothing is left to code. Return the frame.</summary>
    private byte[] EncodeInter(VideoFrame picture)
    {
        // The last picture becomes the reference; this one is built in the other planes
        (y, referenceY) = (referenceY, y);
        (u, referenceU) = (referenceU, u);
        (v, referenceV) = (referenceV, v);
        int q = Math.Clamp(Quantizer, 0, 127);
        int yAc = Vp8Tables.AcQuant[q], uvDc = Math.Min((int)Vp8Tables.DcQuant[q], 132), uvAc = Vp8Tables.AcQuant[q];
        int y2Dc = Vp8Tables.DcQuant[q] * 2, y2Ac = Math.Max(Vp8Tables.AcQuant[q] * 155 / 100, 8);

        // Every macroblock first, as the header carries how many are skipped; vectors kept as the decoder will have them
        int count = rows * columns;
        int[] stored = new int[count * levels.Length];
        bool[] skipped = new bool[count];
        MotionVector[] vectors = new MotionVector[count];
        int[] modes = new int[count];
        MotionVector[] deltas = new MotionVector[count];
        byte[] probabilities = new byte[count * 4];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                // Motion: chosen among zero, the neighbours' and a search, then coded by mode
                int mb = row * columns + column;
                Span<MotionVector> near = stackalloc MotionVector[4];
                Span<int> weights = stackalloc int[4];
                FindNear(vectors, row, column, near, weights);
                for (int i = 0; i < 4; i++) probabilities[mb * 4 + i] = Vp8Tables.MvCountProbs[weights[i] * 4 + i];
                (modes[mb], vectors[mb], deltas[mb]) = ChooseMotion(picture, row, column, near);

                // Prediction copied from the moved place, then the residual
                Array.Clear(coefficients);
                Array.Clear(levels);
                MotionVector mv = vectors[mb];
                CopyMoved(referenceY, y, columns * 16, row * 16, column * 16, 16, mv.X >> 3, mv.Y >> 3);
                CopyMoved(referenceU, u, columns * 8, row * 8, column * 8, 8, mv.X >> 4, mv.Y >> 4);
                CopyMoved(referenceV, v, columns * 8, row * 8, column * 8, 8, mv.X >> 4, mv.Y >> 4);
                EncodeLuma(picture, row, column, yAc, y2Dc, y2Ac, predicted: true);
                EncodeChroma(picture.U, u, picture.ChromaWidth, picture.ChromaHeight, row, column, 16, uvDc, uvAc, predicted: true);
                EncodeChroma(picture.V, v, picture.ChromaWidth, picture.ChromaHeight, row, column, 20, uvDc, uvAc, predicted: true);
                levels.CopyTo(stored, mb * levels.Length);
                skipped[mb] = Array.TrueForAll(levels, l => l == 0);
            }
        }

        // Header with the skip probability, then each macroblock's modes
        BoolEncoder header = new(), tokens = new();
        int notSkipped = skipped.Count(s => !s);
        int skipProbability = Math.Clamp((int)Math.Round(256.0 * notSkipped / count), 1, 255);
        WriteHeader(header, q, keyframe: false, skipProbability);
        for (int mb = 0; mb < count; mb++)
        {
            // Skip flag, inter, the last frame, the motion mode by the neighbours, and a new vector's difference
            header.Write(skipProbability, skipped[mb] ? 1 : 0);
            header.Write(InterProbability, 1);
            header.Write(LastProbability, 0);
            header.WriteTree(Vp8Modes.MvRefTree, probabilities.AsSpan(mb * 4, 4), modes[mb]);
            if (modes[mb] != Vp8Modes.New) continue;
            WriteMotionComponent(header, deltas[mb].Y >> 1, 0);
            WriteMotionComponent(header, deltas[mb].X >> 1, 19);
        }

        // Tokens of the macroblocks not skipped; skipped ones clear their contexts, Y2's too
        int[] above = new int[columns * 9];
        int[] left = new int[9];
        for (int row = 0; row < rows; row++)
        {
            Array.Clear(left);
            for (int column = 0; column < columns; column++)
            {
                int mb = row * columns + column;
                if (skipped[mb])
                {
                    Array.Clear(left);
                    Array.Clear(above, column * 9, 9);
                    continue;
                }
                Array.Copy(stored, mb * levels.Length, levels, 0, levels.Length);
                WriteTokens(tokens, left, above, column * 9);
            }
        }
        // Return the frame
        return Assemble(header.Finish(), tokens.Finish(), picture.Width, picture.Height, keyframe: false);
    }

    /// <summary>Probability that a macroblock of an inter frame is intra, none here.</summary>
    private const int InterProbability = 1;
    /// <summary>Probability that an inter macroblock predicts from the last frame, all here.</summary>
    private const int LastProbability = 255;

    /// <summary>Extra cost of a new vector over the neighbours', in absolute differences, for its coded bits.</summary>
    private const int NewVectorCost = 384;
    /// <summary>Extra cost of the nearest or near vector over none.</summary>
    private const int NearVectorCost = 96;
    /// <summary>Most steps of the diamond search.</summary>
    private const int SearchSteps = 8;

    /// <summary>Copy the <paramref name="size"/> square block at (<paramref name="x"/>, <paramref name="y0"/>) moved by whole pixels from the reference into the same place of the reconstruction.</summary>
    private static void CopyMoved(byte[] from, byte[] to, int stride, int y0, int x, int size, int dx, int dy)
    {
        // Row by row
        for (int row = 0; row < size; row++) Array.Copy(from, (y0 + dy + row) * stride + x + dx, to, (y0 + row) * stride + x, size);
    }

    /// <summary>Find the near vectors of a macroblock as the decoder does, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-16.3">RFC 6386 16.3</see>, from its above, left and above-left neighbours' vectors, all inter from the last frame: <paramref name="near"/> gets best, nearest and near, <paramref name="weights"/> their weights.</summary>
    private void FindNear(MotionVector[] vectors, int row, int column, Span<MotionVector> near, Span<int> weights)
    {
        // Neighbours inside the frame; those outside count as intra, as the decoder's border is
        near.Clear();
        weights.Clear();
        int mv = 0, count = 0;
        if (row > 0)
        {
            MotionVector a = vectors[(row - 1) * columns + column];
            if (!a.IsZero)
            {
                near[++mv] = a;
                ++count;
            }
            weights[count] += 2;
        }
        if (column > 0)
        {
            MotionVector l = vectors[row * columns + column - 1];
            if (!l.IsZero)
            {
                if (!l.Equals(near[mv]))
                {
                    near[++mv] = l;
                    ++count;
                }
                weights[count] += 2;
            }
            else weights[0] += 2;
        }
        if (row > 0 && column > 0)
        {
            MotionVector al = vectors[(row - 1) * columns + column - 1];
            if (!al.IsZero)
            {
                if (!al.Equals(near[mv]))
                {
                    near[++mv] = al;
                    ++count;
                }
                weights[count] += 1;
            }
            else weights[0] += 1;
        }

        // Three distinct: above-left may merge with nearest; no splits here; nearest the more common
        if (weights[3] != 0 && near[mv].Equals(near[1])) weights[1] += 1;
        weights[3] = 0;
        if (weights[2] > weights[1])
        {
            (weights[1], weights[2]) = (weights[2], weights[1]);
            (near[1], near[2]) = (near[2], near[1]);
        }
        // Done
        if (weights[1] >= weights[0]) near[0] = near[1];
    }

    /// <summary>Choose a macroblock's motion: none, the nearest or near vector as clamped, or a searched one, by the luma's absolute differences and the vector's cost; only even whole pixels inside the frame. Return the mode, the vector the decoder will have, and a new vector's difference from the best.</summary>
    private (int Mode, MotionVector Vector, MotionVector Delta) ChooseMotion(VideoFrame picture, int row, int column, ReadOnlySpan<MotionVector> near)
    {
        // Bounds of the decoder's clamping, in eighths
        int toLeft = -128 - column * 128, toRight = (columns - column) * 128, toTop = -128 - row * 128, toBottom = (rows - row) * 128;
        MotionVector nearest = Clamp(near[1], toLeft, toRight, toTop, toBottom), nearer = Clamp(near[2], toLeft, toRight, toTop, toBottom), best = Clamp(near[0], toLeft, toRight, toTop, toBottom);
        int zero = Difference(picture, row, column, 0, 0, int.MaxValue);
        if (!MotionSearch || zero < 512) return (Vp8Modes.Zero, default, default);

        // The neighbours' vectors, when usable
        (int Mode, MotionVector Vector, int Cost) chosen = (Vp8Modes.Zero, default, zero);
        foreach ((int mode, MotionVector candidate) in new[] { (Vp8Modes.Nearest, nearest), (Vp8Modes.Near, nearer) })
        {
            if (candidate.IsZero || !Usable(candidate, row, column)) continue;
            int cost = Difference(picture, row, column, candidate.X >> 3, candidate.Y >> 3, chosen.Cost) + NearVectorCost;
            if (cost < chosen.Cost) chosen = (mode, candidate, cost);
        }

        // A diamond search of two pixels from the best so far
        (int dx, int dy) at = (chosen.Vector.X >> 3, chosen.Vector.Y >> 3);
        int found = Difference(picture, row, column, at.dx, at.dy, int.MaxValue);
        for (int step = 0; step < SearchSteps; step++)
        {
            (int, int) next = at;
            foreach ((int sx, int sy) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2) })
            {
                MotionVector moved = new((at.dx + sx) * 8, (at.dy + sy) * 8);
                if (!Usable(moved, row, column) || Math.Abs(moved.X - best.X) >> 1 > 1023 || Math.Abs(moved.Y - best.Y) >> 1 > 1023) continue;
                int difference = Difference(picture, row, column, at.dx + sx, at.dy + sy, found);
                if (difference >= found) continue;
                found = difference;
                next = (at.dx + sx, at.dy + sy);
            }
            if (next == at) break;
            at = next;
        }
        MotionVector searched = new(at.dx * 8, at.dy * 8);
        if (!searched.IsZero && !searched.Equals(chosen.Vector) && found + NewVectorCost < chosen.Cost) return (Vp8Modes.New, searched, new MotionVector(searched.X - best.X, searched.Y - best.Y));
        // Return the neighbours' or none
        return (chosen.Mode, chosen.Vector, default);
    }

    /// <summary>Return whether a whole-pixel vector of even pixels keeps the macroblock's prediction inside the frame.</summary>
    private bool Usable(MotionVector mv, int row, int column)
    {
        // Eighths that are even whole pixels, and the block inside
        if ((mv.X & 15) != 0 || (mv.Y & 15) != 0) return false;
        int x = column * 16 + (mv.X >> 3), y0 = row * 16 + (mv.Y >> 3);
        // Return whether inside
        return x >= 0 && y0 >= 0 && x + 16 <= columns * 16 && y0 + 16 <= rows * 16;
    }

    /// <summary>Return the sum of absolute differences between the source's luma macroblock and the reference moved by (<paramref name="dx"/>, <paramref name="dy"/>); stop early past <paramref name="limit"/>.</summary>
    private int Difference(VideoFrame picture, int row, int column, int dx, int dy, int limit)
    {
        // Row by row, the source clamped at its edges
        int stride = columns * 16, sum = 0;
        for (int r = 0; r < 16; r++)
        {
            int sy = Math.Min(row * 16 + r, picture.Height - 1);
            int reference = (row * 16 + r + dy) * stride + column * 16 + dx;
            for (int c = 0; c < 16; c++) sum += Math.Abs(picture.Y[sy * picture.Width + Math.Min(column * 16 + c, picture.Width - 1)] - referenceY[reference + c]);
            if (sum >= limit) return sum;
        }
        // Return sum
        return sum;
    }

    /// <summary>Return <paramref name="mv"/> clamped to the bounds, as the decoder clamps near vectors.</summary>
    private static MotionVector Clamp(MotionVector mv, int toLeft, int toRight, int toTop, int toBottom) =>
        new(mv.X < toLeft ? toLeft : mv.X > toRight ? toRight : mv.X, mv.Y < toTop ? toTop : mv.Y > toBottom ? toBottom : mv.Y);

    /// <summary>Write one component of a vector's difference, in quarter pixels, with the motion vector probabilities at <paramref name="probs"/>, as the decoder reads it: short values by a tree, long ones bit by bit with bit 3 implied below 16, then the sign.</summary>
    private static void WriteMotionComponent(BoolEncoder bw, int value, int probs)
    {
        // Short or long
        const int IsShort = 0, Sign = 1, Short = 2, Bits = Short + 8 - 1, LongWidth = 10;
        ReadOnlySpan<byte> p = Vp8Tables.DefaultMvProbs;
        int magnitude = Math.Abs(value);
        if (magnitude < 8)
        {
            bw.Write(p[probs + IsShort], 0);
            bw.WriteTree(Vp8Modes.SmallMvTree, p.Slice(probs + Short, 7), magnitude);
        }
        else
        {
            bw.Write(p[probs + IsShort], 1);
            for (int i = 0; i < 3; i++) bw.Write(p[probs + Bits + i], magnitude >> i & 1);
            for (int i = LongWidth - 1; i > 3; i--) bw.Write(p[probs + Bits + i], magnitude >> i & 1);
            if ((magnitude & 0xFFF0) != 0) bw.Write(p[probs + Bits + 3], magnitude >> 3 & 1);
        }
        // Done
        if (magnitude != 0) bw.Write(p[probs + Sign], value < 0 ? 1 : 0);
    }

    /// <summary>Write the first partition's frame header: no segmentation, loop filter or extra partitions, one quantizer and no probability updates; keyframes without skipping, inter frames from the last frame only, with skipping at <paramref name="skipProbability"/>.</summary>
    private static void WriteHeader(BoolEncoder header, int q, bool keyframe, int skipProbability = 0)
    {
        // Keyframes' color space and clamping, then segmentation, loop filter type, level and sharpness, its deltas, one token partition
        if (keyframe) header.WriteLiteral(0, 2);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 6);
        header.WriteLiteral(0, 3);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 2);

        // Quantizer without deltas; inter frames keep golden and altref as they are and refresh the last frame
        header.WriteLiteral(q, 7);
        header.WriteLiteral(0, 5);
        if (!keyframe)
        {
            header.WriteLiteral(0, 2);
            header.WriteLiteral(0, 2);
            header.WriteLiteral(0, 2);
            header.WriteLiteral(0, 2);
        }
        header.WriteLiteral(1, 1);
        if (!keyframe) header.WriteLiteral(1, 1);

        // No coefficient updates; skip flags on inter frames only
        ReadOnlySpan<byte> updates = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < updates.Length; i++) header.Write(updates[i], 0);
        if (keyframe)
        {
            header.WriteLiteral(0, 1);
            return;
        }
        header.WriteLiteral(1, 1);
        header.WriteLiteral(skipProbability, 8);

        // Done: inter, last and golden probabilities, and no mode or motion vector updates
        header.WriteLiteral(InterProbability, 8);
        header.WriteLiteral(LastProbability, 8);
        header.WriteLiteral(128, 8);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 1);
        ReadOnlySpan<byte> mvUpdates = Vp8Tables.MvUpdateProbs;
        for (int i = 0; i < mvUpdates.Length; i++) header.Write(mvUpdates[i], 0);
    }

    /// <summary>Predict a luma macroblock by DC, transform and quantize its residual with the DCs in Y2, and reconstruct it.</summary>
    private void EncodeLuma(VideoFrame picture, int row, int column, int yAc, int y2Dc, int y2Ac, bool predicted = false)
    {
        // Prediction into the reconstruction, as the decoder does, unless there already
        int stride = columns * 16, at = row * 16 * stride + column * 16;
        if (!predicted) PredictDc(y, at, stride, 16, row, column);

        // Each 4x4 block: its AC quantized, its DC for Y2
        Span<short> residual = stackalloc short[16];
        Span<int> transformed = stackalloc int[16];
        Span<int> dcs = stackalloc int[16];
        for (int block = 0; block < 16; block++)
        {
            int blockAt = at + (block >> 2) * 4 * stride + (block & 3) * 4;
            Residual(picture.Y, picture.Width, picture.Height, column * 16 + (block & 3) * 4, row * 16 + (block >> 2) * 4, y, blockAt, stride, residual);
            ForwardDct(residual, transformed);
            dcs[block] = transformed[0];
            for (int k = 1; k < 16; k++) Quantize(block * 16 + k, transformed[k], yAc, dc: false);
        }

        // Y2: the DCs transformed and quantized, then back into the blocks
        Span<int> walsh = stackalloc int[16];
        ForwardWalsh(dcs, walsh);
        for (int k = 0; k < 16; k++) Quantize(24 * 16 + k, walsh[k], k == 0 ? y2Dc : y2Ac, dc: k == 0);
        Vp8Transform.InverseWalsh(coefficients, 24 * 16, 0);
        // Done: the blocks reconstructed in place
        for (int block = 0; block < 16; block++)
        {
            int blockAt = at + (block >> 2) * 4 * stride + (block & 3) * 4;
            Vp8Transform.InverseDctAdd(coefficients, block * 16, y, blockAt, y, blockAt, stride);
        }
    }

    /// <summary>Predict a chroma macroblock by DC, transform and quantize its four blocks from <paramref name="first"/>, and reconstruct it.</summary>
    private void EncodeChroma(byte[] source, byte[] plane, int sourceWidth, int sourceHeight, int row, int column, int first, int dcFactor, int acFactor, bool predicted = false)
    {
        // Prediction into the reconstruction, unless there already
        int stride = columns * 8, at = row * 8 * stride + column * 8;
        if (!predicted) PredictDc(plane, at, stride, 8, row, column);

        // Each block quantized, then reconstructed
        Span<short> residual = stackalloc short[16];
        Span<int> transformed = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int block = first + i, blockAt = at + (i >> 1) * 4 * stride + (i & 1) * 4;
            Residual(source, sourceWidth, sourceHeight, column * 8 + (i & 1) * 4, row * 8 + (i >> 1) * 4, plane, blockAt, stride, residual);
            ForwardDct(residual, transformed);
            for (int k = 0; k < 16; k++) Quantize(block * 16 + k, transformed[k], k == 0 ? dcFactor : acFactor, dc: k == 0);
            // Done
            Vp8Transform.InverseDctAdd(coefficients, block * 16, plane, blockAt, plane, blockAt, stride);
        }
    }

    /// <summary>Fill a block with the DC prediction as the decoder makes it: the average of the above row and left column; on the top row the left column only, on the left column the above row only, and 128 in the corner.</summary>
    private static void PredictDc(byte[] plane, int at, int stride, int size, int row, int column)
    {
        // The edges the decoder substitutes
        int sum = 0;
        for (int i = 0; i < size; i++)
        {
            int leftPixel = column > 0 ? plane[at - 1 + i * stride] : row > 0 ? plane[at - stride + i] : 129;
            int abovePixel = row > 0 ? plane[at - stride + i] : column > 0 ? plane[at - 1 + i * stride] : 127;
            sum += leftPixel + abovePixel;
        }
        int shift = size == 16 ? 5 : 4;
        byte dc = (byte)((sum + (1 << (shift - 1))) >> shift);
        // Done
        for (int i = 0; i < size; i++) plane.AsSpan(at + i * stride, size).Fill(dc);
    }

    /// <summary>Return the source minus the prediction of a 4x4 block at <paramref name="x"/>, <paramref name="y0"/>; the source repeats its edge pixels past its size.</summary>
    private static void Residual(byte[] source, int sourceWidth, int sourceHeight, int x, int y0, byte[] prediction, int at, int stride, Span<short> residual)
    {
        // Row by row, clamped into the source
        for (int r = 0; r < 4; r++)
        {
            int sy = Math.Min(y0 + r, sourceHeight - 1);
            for (int c = 0; c < 4; c++) residual[r * 4 + c] = (short)(source[sy * sourceWidth + Math.Min(x + c, sourceWidth - 1)] - prediction[at + r * stride + c]);
        }
    }

    /// <summary>Quantize <paramref name="value"/> by <paramref name="factor"/> into the levels, and its dequantized value into the coefficients; AC with a dead zone, which saves bits on noise.</summary>
    private void Quantize(int index, int value, int factor, bool dc)
    {
        // Rounded, or toward zero by a third for AC
        int magnitude = Math.Abs(value);
        int level = Math.Min((magnitude + (dc ? factor / 2 : factor / 3)) / factor, MaxLevel);
        if (value < 0) level = -level;
        levels[index] = level;
        // Done
        coefficients[index] = (short)(level * factor);
    }

    /// <summary>The forward DCT of libvpx, <c>vp8_short_fdct4x4_c</c>.</summary>
    private static void ForwardDct(ReadOnlySpan<short> input, Span<int> output)
    {
        // Rows
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int r = i * 4;
            int a1 = (input[r] + input[r + 3]) * 8, b1 = (input[r + 1] + input[r + 2]) * 8, c1 = (input[r + 1] - input[r + 2]) * 8, d1 = (input[r] - input[r + 3]) * 8;
            temp[r] = a1 + b1;
            temp[r + 2] = a1 - b1;
            temp[r + 1] = (c1 * 2217 + d1 * 5352 + 14500) >> 12;
            temp[r + 3] = (d1 * 2217 - c1 * 5352 + 7500) >> 12;
        }

        // Columns
        for (int i = 0; i < 4; i++)
        {
            int a1 = temp[i] + temp[12 + i], b1 = temp[4 + i] + temp[8 + i], c1 = temp[4 + i] - temp[8 + i], d1 = temp[i] - temp[12 + i];
            output[i] = (a1 + b1 + 7) >> 4;
            output[8 + i] = (a1 - b1 + 7) >> 4;
            output[4 + i] = ((c1 * 2217 + d1 * 5352 + 12000) >> 16) + (d1 != 0 ? 1 : 0);
            // Done
            output[12 + i] = (d1 * 2217 - c1 * 5352 + 51000) >> 16;
        }
    }

    /// <summary>The forward Walsh-Hadamard transform of libvpx, <c>vp8_short_walsh4x4_c</c>, of the 16 luma DCs in raster order.</summary>
    private static void ForwardWalsh(ReadOnlySpan<int> input, Span<int> output)
    {
        // Rows
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int r = i * 4;
            int a1 = (input[r] + input[r + 2]) * 4, d1 = (input[r + 1] + input[r + 3]) * 4, c1 = (input[r + 1] - input[r + 3]) * 4, b1 = (input[r] - input[r + 2]) * 4;
            temp[r] = a1 + d1 + (a1 != 0 ? 1 : 0);
            temp[r + 1] = b1 + c1;
            temp[r + 2] = b1 - c1;
            temp[r + 3] = a1 - d1;
        }

        // Columns
        for (int i = 0; i < 4; i++)
        {
            int a1 = temp[i] + temp[8 + i], d1 = temp[4 + i] + temp[12 + i], c1 = temp[4 + i] - temp[12 + i], b1 = temp[i] - temp[8 + i];
            int a2 = a1 + d1, b2 = b1 + c1, c2 = b1 - c1, d2 = a1 - d1;
            a2 += a2 < 0 ? 1 : 0;
            b2 += b2 < 0 ? 1 : 0;
            c2 += c2 < 0 ? 1 : 0;
            d2 += d2 < 0 ? 1 : 0;
            output[i] = (a2 + 3) >> 3;
            output[4 + i] = (b2 + 3) >> 3;
            output[8 + i] = (c2 + 3) >> 3;
            // Done
            output[12 + i] = (d2 + 3) >> 3;
        }
    }

    /// <summary>Write the macroblock's tokens: Y2, 16 luma from their first AC, 4 U and 4 V, each in the context of its neighbors.</summary>
    private void WriteTokens(BoolEncoder bw, int[] left, int[] above, int aboveAt)
    {
        // Y2 first, then luma, then chroma
        foreach ((int first, int stop, int type, int start) in new[] { (24, 25, 1, 0), (0, 16, 0, 1), (16, 24, 2, 0) })
        {
            for (int i = first; i < stop; i++)
            {
                int context = left[LeftContextIndex[i]] + above[aboveAt + AboveContextIndex[i]];
                int nonzero = WriteBlock(bw, type * 8 * 3 * 11, context, start, i * 16) ? 1 : 0;
                left[LeftContextIndex[i]] = above[aboveAt + AboveContextIndex[i]] = nonzero;
            }
        }
    }

    /// <summary>Write one block's levels in zigzag order from <paramref name="start"/>, as the decoder reads them; return whether it has any.</summary>
    private bool WriteBlock(BoolEncoder bw, int typeProbs, int context, int start, int block)
    {
        // The last level that is not zero
        ReadOnlySpan<byte> probs = Vp8Tables.DefaultCoeffProbs;
        int last = -1;
        for (int c = 15; c >= start; c--)
        {
            if (levels[block + ZigZag[c]] == 0) continue;
            last = c;
            break;
        }

        // Tokens: end of block unless after a zero, zero, or a value with its sign
        int p = typeProbs + Bands[start] * 33 + context * 11;
        bool afterZero = false;
        for (int c = start; ; c++)
        {
            if (!afterZero)
            {
                bw.Write(probs[p], c <= last ? 1 : 0);
                if (c > last) break;
            }
            int level = levels[block + ZigZag[c]];
            if (level == 0)
            {
                bw.Write(probs[p + 1], 0);
                afterZero = true;
                p = typeProbs + Bands[c + 1] * 33;
                continue;
            }
            bw.Write(probs[p + 1], 1);
            int magnitude = Math.Abs(level);
            if (magnitude == 1) bw.Write(probs[p + 2], 0);
            else
            {
                bw.Write(probs[p + 2], 1);
                WriteLargeToken(bw, probs, p, magnitude);
            }
            bw.Write(128, level < 0 ? 1 : 0);
            if (c == 15) break;
            p = typeProbs + Bands[c + 1] * 33 + (magnitude == 1 ? 1 : 2) * 11;
            afterZero = false;
        }
        // Return whether any
        return last >= start;
    }

    /// <summary>Write a value of two or more: its token, then a category's extra bits.</summary>
    private static void WriteLargeToken(BoolEncoder bw, ReadOnlySpan<byte> probs, int p, int value)
    {
        // Two to four
        if (value <= 4)
        {
            bw.Write(probs[p + 3], 0);
            if (value == 2)
            {
                bw.Write(probs[p + 4], 0);
                return;
            }
            bw.Write(probs[p + 4], 1);
            bw.Write(probs[p + 5], value == 3 ? 0 : 1);
            return;
        }

        // Categories 1 and 2
        bw.Write(probs[p + 3], 1);
        if (value <= 10)
        {
            bw.Write(probs[p + 6], 0);
            if (value <= 6)
            {
                bw.Write(probs[p + 7], 0);
                bw.Write(159, value - 5);
                return;
            }
            bw.Write(probs[p + 7], 1);
            bw.Write(165, (value - 7) >> 1);
            bw.Write(145, (value - 7) & 1);
            return;
        }

        // Categories 3 to 6
        bw.Write(probs[p + 6], 1);
        if (value <= 34)
        {
            bw.Write(probs[p + 8], 0);
            if (value <= 18)
            {
                bw.Write(probs[p + 9], 0);
                ExtraBits(bw, value - 11, [140, 148, 173]);
                return;
            }
            bw.Write(probs[p + 9], 1);
            ExtraBits(bw, value - 19, [135, 140, 155, 176]);
            return;
        }
        bw.Write(probs[p + 8], 1);
        if (value <= 66)
        {
            bw.Write(probs[p + 10], 0);
            ExtraBits(bw, value - 35, [130, 134, 141, 157, 180]);
            return;
        }
        bw.Write(probs[p + 10], 1);
        // Done
        ExtraBits(bw, value - 67, [129, 130, 133, 140, 153, 177, 196, 230, 243, 254, 254]);
    }

    /// <summary>Write extra bits, most significant first, with <paramref name="probs"/> indexed by bit.</summary>
    private static void ExtraBits(BoolEncoder bw, int value, ReadOnlySpan<byte> probs)
    {
        // From the highest bit down
        for (int bit = probs.Length - 1; bit >= 0; bit--) bw.Write(probs[bit], value >> bit & 1);
    }

    /// <summary>Return the frame: the uncompressed header, of ten bytes for a keyframe and three for an inter frame, the first partition, then the token partition.</summary>
    private static byte[] Assemble(byte[] first, byte[] tokens, int width, int height, bool keyframe)
    {
        // Frame tag: the frame type, version 0, shown, the first partition's size
        int headerLength = keyframe ? 10 : 3;
        byte[] frame = new byte[headerLength + first.Length + tokens.Length];
        int tag = (keyframe ? 0 : 1) | 0 << 1 | 1 << 4 | first.Length << 5;
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        if (!keyframe)
        {
            first.CopyTo(frame, 3);
            tokens.CopyTo(frame, 3 + first.Length);
            return frame;
        }

        // Keyframes: the start code and the dimensions without scaling
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        frame[6] = (byte)width;
        frame[7] = (byte)(width >> 8);
        frame[8] = (byte)height;
        frame[9] = (byte)(height >> 8);
        first.CopyTo(frame, 10);
        tokens.CopyTo(frame, 10 + first.Length);
        // Return frame
        return frame;
    }

    /// <summary>Make the reconstruction planes for a picture's size, in whole macroblocks.</summary>
    private void Allocate(int pictureWidth, int pictureHeight)
    {
        // Only when the size changes
        if (pictureWidth == width && pictureHeight == height) return;
        (width, height) = (pictureWidth, pictureHeight);
        columns = (width + 15) / 16;
        rows = (height + 15) / 16;
        y = new byte[columns * 16 * rows * 16];
        u = new byte[columns * 8 * rows * 8];
        v = new byte[columns * 8 * rows * 8];
        referenceY = new byte[y.Length];
        referenceU = new byte[u.Length];
        // Done
        referenceV = new byte[v.Length];
    }

    /// <summary>Return the reconstruction cropped to the picture's size.</summary>
    private VideoFrame Crop()
    {
        // Rows of each plane
        int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        byte[] cy = new byte[width * height], cu = new byte[chromaWidth * chromaHeight], cv = new byte[chromaWidth * chromaHeight];
        for (int r = 0; r < height; r++) y.AsSpan(r * columns * 16, width).CopyTo(cy.AsSpan(r * width));
        for (int r = 0; r < chromaHeight; r++)
        {
            u.AsSpan(r * columns * 8, chromaWidth).CopyTo(cu.AsSpan(r * chromaWidth));
            v.AsSpan(r * columns * 8, chromaWidth).CopyTo(cv.AsSpan(r * chromaWidth));
        }
        // Return picture
        return new VideoFrame(width, height, cy, cu, cv);
    }
}
