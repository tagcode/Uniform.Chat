namespace Uniform.Matrix.Video.Vp8;

/// <summary>Encodes pictures as VP8 frames, <see href="https://www.rfc-editor.org/rfc/rfc6386">RFC 6386</see>, in pure C#: for now keyframes only, every macroblock predicted from its edges by DC, with one quantizer, the default probabilities and no loop filter.</summary>
/// <remarks>The encoder predicts and reconstructs as a decoder does, with <see cref="Vp8Decoder"/>'s inverse transforms, so its <see cref="Reconstruction"/> is the picture every decoder shows. Each frame stands alone, which costs bits but needs no reference and recovers at once from loss.</remarks>
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

    /// <summary>The last frame as decoders show it.</summary>
    public VideoFrame Reconstruction => Crop();

    /// <summary>Encode <paramref name="picture"/> as a keyframe; return the frame.</summary>
    public byte[] Encode(VideoFrame picture)
    {
        // Planes of whole macroblocks
        Allocate(picture.Width, picture.Height);
        BoolEncoder header = new(), tokens = new();
        int q = Math.Clamp(Quantizer, 0, 127);
        WriteHeader(header, q);

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
        return Assemble(header.Finish(), tokens.Finish(), picture.Width, picture.Height);
    }

    /// <summary>Write the first partition's frame header: no segmentation, loop filter or extra partitions, one quantizer, no probability updates, and no skipping.</summary>
    private static void WriteHeader(BoolEncoder header, int q)
    {
        // Color space and clamping, segmentation, loop filter type, level and sharpness, its deltas, one token partition
        header.WriteLiteral(0, 2);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 6);
        header.WriteLiteral(0, 3);
        header.WriteLiteral(0, 1);
        header.WriteLiteral(0, 2);

        // Quantizer without deltas, keeping the probabilities, none updated, no skip flags
        header.WriteLiteral(q, 7);
        header.WriteLiteral(0, 5);
        header.WriteLiteral(1, 1);
        ReadOnlySpan<byte> updates = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < updates.Length; i++) header.Write(updates[i], 0);
        // Done
        header.WriteLiteral(0, 1);
    }

    /// <summary>Predict a luma macroblock by DC, transform and quantize its residual with the DCs in Y2, and reconstruct it.</summary>
    private void EncodeLuma(VideoFrame picture, int row, int column, int yAc, int y2Dc, int y2Ac)
    {
        // Prediction into the reconstruction, as the decoder does
        int stride = columns * 16, at = row * 16 * stride + column * 16;
        PredictDc(y, at, stride, 16, row, column);

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
    private void EncodeChroma(byte[] source, byte[] plane, int sourceWidth, int sourceHeight, int row, int column, int first, int dcFactor, int acFactor)
    {
        // Prediction into the reconstruction
        int stride = columns * 8, at = row * 8 * stride + column * 8;
        PredictDc(plane, at, stride, 8, row, column);

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

    /// <summary>Return the frame: the uncompressed keyframe header, the first partition, then the token partition.</summary>
    private static byte[] Assemble(byte[] first, byte[] tokens, int width, int height)
    {
        // Frame tag: keyframe, version 0, shown, the first partition's size; start code; dimensions without scaling
        byte[] frame = new byte[10 + first.Length + tokens.Length];
        int tag = 0 | 0 << 1 | 1 << 4 | first.Length << 5;
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
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
        // Done
        v = new byte[columns * 8 * rows * 8];
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
