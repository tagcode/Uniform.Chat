namespace Uniform.Matrix.Video.Vp8;

// DCT coefficient tokens, RFC 6386 section 13
public sealed partial class Vp8Decoder
{
    /// <summary>Block of each token context entry, left: 4 luma rows, 2 of U, 2 of V, then Y2.</summary>
    private static ReadOnlySpan<byte> LeftContextIndex => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    /// <summary>Block of each token context entry, above: 4 luma columns, 2 of U, 2 of V, then Y2.</summary>
    private static ReadOnlySpan<byte> AboveContextIndex => [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];
    /// <summary>Probability band of each coefficient position.</summary>
    private static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7];
    /// <summary>Raster position of each coefficient in zigzag order.</summary>
    private static ReadOnlySpan<byte> ZigZag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>Read the coefficients of macroblock row <paramref name="row"/> from its <paramref name="partition"/> into <see cref="coefficients"/>, dequantized.</summary>
    private void DecodeTokens(BoolDecoder partition, int row)
    {
        // Contexts start empty at the top and the left
        if (row == 0) Array.Clear(aboveContext);
        Array.Clear(leftContext);
        Array.Clear(coefficients);
        for (int column = 0; column < columns; column++)
        {
            // A macroblock without coefficients clears its contexts, Y2's only when it has one
            ref MacroblockInfo mb = ref macroblocks[MacroblockIndex(row, column)];
            int above = column * 9;
            if (mb.SkipCoefficients)
            {
                Array.Clear(leftContext, 0, 8);
                Array.Clear(aboveContext, above, 8);
                if (mb.YMode != Vp8Modes.Subblocks && mb.YMode != Vp8Modes.Split)
                {
                    leftContext[8] = 0;
                    aboveContext[above + 8] = 0;
                }
                mb.EobMask = 0;
                continue;
            }
            // Done
            mb.EobMask = (uint)DecodeMacroblockTokens(partition, above, column * CoefficientsPerMacroblock, mb.YMode, (segmentationEnabled ? mb.SegmentId : 0) * 6);
        }
    }

    /// <summary>Read the 25 blocks of a macroblock: Y2 first when the mode has one, then 16 luma, 4 U and 4 V; return the mask of blocks with coefficients past the DC, with bit 31 when any block has any.</summary>
    private int DecodeMacroblockTokens(BoolDecoder bd, int above, int blocks, int mode, int factors)
    {
        // With Y2 the luma blocks start at their first AC coefficient
        bool hasY2 = mode != Vp8Modes.Subblocks && mode != Vp8Modes.Split;
        int i = hasY2 ? 24 : 0, stop = hasY2 ? 24 : 16, type = hasY2 ? 1 : 3;
        int tokens = blocks + (hasY2 ? 24 * 16 : 0);
        int dc = factors + (hasY2 ? 4 : 0);
        byte[] probs = entropy.CoeffProbs;
        int eobMask = 0;
        while (true)
        {
            // One block in its context
            int typeProbs = type * 8 * 3 * 11;
            int start = type == 0 ? 1 : 0;
            int c = DecodeBlock(bd, probs, typeProbs, leftContext[LeftContextIndex[i]] + aboveContext[above + AboveContextIndex[i]], start, tokens, dc);
            if (c > 1) eobMask |= 1 << i;
            int nonzero = c != start ? 1 : 0;
            eobMask |= nonzero << 31;
            leftContext[LeftContextIndex[i]] = aboveContext[above + AboveContextIndex[i]] = nonzero;
            tokens += 16;
            i++;

            // Next block, or the next kind of blocks
            if (i < stop) continue;
            if (i == 25)
            {
                type = 0;
                i = 0;
                stop = 16;
                tokens = blocks;
                dc = factors;
                continue;
            }
            if (i == 16)
            {
                type = 2;
                stop = 24;
                dc = factors + 2;
                continue;
            }
            // Return mask
            return eobMask;
        }
    }

    /// <summary>Read one block's tokens into <paramref name="tokens"/>, dequantized by the factors at <paramref name="dc"/> (DC, then AC); return the position after the last token, as the reference decoder counts it.</summary>
    private int DecodeBlock(BoolDecoder bd, byte[] probs, int typeProbs, int context, int c, int tokens, int dc)
    {
        // The first token may be the end of block
        int p = typeProbs + Bands[c] * 33 + context * 11;
        while (bd.Read(probs[p]) != 0)
        {
            // Zeros, which cannot be followed by the end of block
            while (bd.Read(probs[p + 1]) == 0)
            {
                if (c == 15) return c;
                c++;
                p = typeProbs + Bands[c] * 33;
            }

            // A non-zero value: one, two to four, or a category with extra bits
            int value, next;
            if (bd.Read(probs[p + 2]) == 0)
            {
                value = 1;
                next = 1;
            }
            else
            {
                value = ReadLargeToken(bd, probs, p);
                next = 2;
            }

            // Sign and dequantization; the last position ends the block
            int signed = bd.ReadBit() != 0 ? -value : value;
            coefficients[tokens + ZigZag[c]] = (short)(signed * dequant[dc + (c > 0 ? 1 : 0)]);
            if (c == 15) return c;
            c++;
            p = typeProbs + Bands[c] * 33 + next * 11;
        }
        // Return the end position
        return c;
    }

    /// <summary>Read a token larger than one, at probabilities <paramref name="p"/>, with its extra bits; return its value.</summary>
    private static int ReadLargeToken(BoolDecoder bd, byte[] probs, int p)
    {
        // Two to four
        if (bd.Read(probs[p + 3]) == 0)
        {
            if (bd.Read(probs[p + 4]) == 0) return 2;
            return bd.Read(probs[p + 5]) == 0 ? 3 : 4;
        }

        // Categories 1 and 2: 5 to 6, 7 to 10
        if (bd.Read(probs[p + 6]) == 0)
        {
            if (bd.Read(probs[p + 7]) == 0) return 5 + bd.Read(159);
            return 7 + (bd.Read(165) << 1) + bd.Read(145);
        }

        // Categories 3 and 4: 11 to 18, 19 to 34
        if (bd.Read(probs[p + 8]) == 0)
        {
            if (bd.Read(probs[p + 9]) == 0) return 11 + ExtraBits(bd, [140, 148, 173]);
            return 19 + ExtraBits(bd, [135, 140, 155, 176]);
        }

        // Categories 5 and 6: 35 to 66, 67 to 2114
        if (bd.Read(probs[p + 10]) == 0) return 35 + ExtraBits(bd, [130, 134, 141, 157, 180]);
        // Return the largest category's
        return 67 + ExtraBits(bd, [129, 130, 133, 140, 153, 177, 196, 230, 243, 254, 254]);
    }

    /// <summary>Read extra bits, most significant first, with <paramref name="probs"/> indexed by bit.</summary>
    private static int ExtraBits(BoolDecoder bd, ReadOnlySpan<byte> probs)
    {
        // From the highest bit down
        int value = 0;
        for (int bit = probs.Length - 1; bit >= 0; bit--) value += bd.Read(probs[bit]) << bit;
        // Return value
        return value;
    }
}
