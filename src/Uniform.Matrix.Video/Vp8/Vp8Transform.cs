namespace Uniform.Matrix.Video.Vp8;

/// <summary>The inverse transforms of VP8, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-14">RFC 6386 section 14</see>: the Walsh-Hadamard transform of the second-order DC block and the 4x4 DCT, exact to the reference decoder.</summary>
internal static class Vp8Transform
{
    private const int CosPi8Sqrt2Minus1 = 20091;
    private const int SinPi8Sqrt2 = 35468;

    /// <summary>Inverse Walsh-Hadamard transform of the 16 coefficients at <paramref name="input"/>, into the DC of each of the 16 luma blocks at <paramref name="coefficients"/>, 16 apart.</summary>
    public static void InverseWalsh(short[] coefficients, int input, int lumaBlocks)
    {
        // Columns into a temporary, kept to 16 bits as the reference does
        Span<short> output = stackalloc short[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = coefficients[input + i] + coefficients[input + i + 12];
            int b1 = coefficients[input + i + 4] + coefficients[input + i + 8];
            int c1 = coefficients[input + i + 4] - coefficients[input + i + 8];
            int d1 = coefficients[input + i] - coefficients[input + i + 12];
            output[i] = (short)(a1 + b1);
            output[4 + i] = (short)(c1 + d1);
            output[8 + i] = (short)(a1 - b1);
            output[12 + i] = (short)(d1 - c1);
        }

        // Rows, each value the DC of a luma block
        for (int i = 0; i < 4; i++)
        {
            int row = i * 4;
            int a1 = output[row] + output[row + 3];
            int b1 = output[row + 1] + output[row + 2];
            int c1 = output[row + 1] - output[row + 2];
            int d1 = output[row] - output[row + 3];
            coefficients[lumaBlocks + (row + 0) * 16] = (short)((a1 + b1 + 3) >> 3);
            coefficients[lumaBlocks + (row + 1) * 16] = (short)((c1 + d1 + 3) >> 3);
            coefficients[lumaBlocks + (row + 2) * 16] = (short)((a1 - b1 + 3) >> 3);
            coefficients[lumaBlocks + (row + 3) * 16] = (short)((d1 - c1 + 3) >> 3);
        }
    }

    /// <summary>Inverse DCT of the 16 coefficients at <paramref name="block"/>, added to the 4x4 prediction at <paramref name="prediction"/> and written to <paramref name="output"/>, clamped to bytes; both use <paramref name="stride"/>.</summary>
    public static void InverseDctAdd(short[] coefficients, int block, byte[] prediction, int predictionIndex, byte[] output, int outputIndex, int stride)
    {
        // Columns
        Span<short> temp = stackalloc short[16];
        for (int i = 0; i < 4; i++)
        {
            int ip0 = coefficients[block + i], ip4 = coefficients[block + 4 + i], ip8 = coefficients[block + 8 + i], ip12 = coefficients[block + 12 + i];
            int a1 = ip0 + ip8;
            int b1 = ip0 - ip8;
            int c1 = (ip4 * SinPi8Sqrt2 >> 16) - (ip12 + (ip12 * CosPi8Sqrt2Minus1 >> 16));
            int d1 = ip4 + (ip4 * CosPi8Sqrt2Minus1 >> 16) + (ip12 * SinPi8Sqrt2 >> 16);
            temp[i] = (short)(a1 + d1);
            temp[12 + i] = (short)(a1 - d1);
            temp[4 + i] = (short)(b1 + c1);
            temp[8 + i] = (short)(b1 - c1);
        }

        // Rows, added to the prediction
        for (int i = 0; i < 4; i++)
        {
            int c0 = temp[i * 4], c1v = temp[i * 4 + 1], c2 = temp[i * 4 + 2], c3 = temp[i * 4 + 3];
            int a1 = c0 + c2;
            int b1 = c0 - c2;
            int c1 = (c1v * SinPi8Sqrt2 >> 16) - (c3 + (c3 * CosPi8Sqrt2Minus1 >> 16));
            int d1 = c1v + (c1v * CosPi8Sqrt2Minus1 >> 16) + (c3 * SinPi8Sqrt2 >> 16);
            int p = predictionIndex + i * stride, o = outputIndex + i * stride;
            output[o] = Clamp(prediction[p] + ((a1 + d1 + 4) >> 3));
            output[o + 3] = Clamp(prediction[p + 3] + ((a1 - d1 + 4) >> 3));
            output[o + 1] = Clamp(prediction[p + 1] + ((b1 + c1 + 4) >> 3));
            output[o + 2] = Clamp(prediction[p + 2] + ((b1 - c1 + 4) >> 3));
        }
    }

    /// <summary>Return <paramref name="value"/> clamped to 0 to 255.</summary>
    public static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
