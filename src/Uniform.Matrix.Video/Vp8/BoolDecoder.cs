namespace Uniform.Matrix.Video.Vp8;

/// <summary>The boolean entropy decoder of VP8, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-7">RFC 6386 section 7</see>: reads bools, each with a probability of being 0 out of 256, from one partition.</summary>
/// <remarks>Past the end of the partition it reads zeros, as the reference decoder does.</remarks>
internal sealed class BoolDecoder
{
    private byte[] data = [];
    private int position;
    private int end;
    private uint range;
    private uint value;
    private int bitCount;

    /// <summary>Start reading <paramref name="length"/> bytes of <paramref name="buffer"/> at <paramref name="start"/>.</summary>
    public void Init(byte[] buffer, int start, int length)
    {
        // The first two bytes fill the value
        data = buffer;
        if (length >= 2)
        {
            value = (uint)(buffer[start] << 8 | buffer[start + 1]);
            position = start + 2;
            end = start + length;
        }
        else
        {
            value = 0;
            position = end = start;
        }
        range = 255;
        // Done
        bitCount = 0;
    }

    /// <summary>Return a bool that is 0 with <paramref name="probability"/> out of 256.</summary>
    public int Read(int probability)
    {
        // Split the range by the probability
        uint split = 1 + (uint)((range - 1) * probability >> 8);
        uint bigSplit = split << 8;
        int bit;
        if (value >= bigSplit)
        {
            bit = 1;
            range -= split;
            value -= bigSplit;
        }
        else
        {
            bit = 0;
            range = split;
        }

        // Shift out bits until the range is at least 128, taking in a byte every 8
        while (range < 128)
        {
            value <<= 1;
            range <<= 1;
            if (++bitCount != 8) continue;
            bitCount = 0;
            if (position < end) value |= data[position++];
        }
        // Return bool
        return bit;
    }

    /// <summary>Return a bool of even probability.</summary>
    public int ReadBit() => Read(128);

    /// <summary>Return an unsigned value of <paramref name="bits"/> bits, most significant first.</summary>
    public int ReadLiteral(int bits)
    {
        // Bit by bit
        int result = 0;
        for (int bit = bits - 1; bit >= 0; bit--) result |= ReadBit() << bit;
        // Return value
        return result;
    }

    /// <summary>Return a value of <paramref name="bits"/> bits followed by a sign bit.</summary>
    public int ReadSigned(int bits)
    {
        // Magnitude, then sign
        int magnitude = ReadLiteral(bits);
        // Return value
        return ReadBit() != 0 ? -magnitude : magnitude;
    }

    /// <summary>Return a signed value of <paramref name="bits"/> bits when a flag bit says one follows, else 0.</summary>
    public int ReadOptionalSigned(int bits) => ReadBit() != 0 ? ReadSigned(bits) : 0;

    /// <summary>Return the leaf of <paramref name="tree"/> reached by bools with <paramref name="probabilities"/>, one per node pair; leaves are stored negated.</summary>
    public int ReadTree(ReadOnlySpan<int> tree, ReadOnlySpan<byte> probabilities)
    {
        // Walk until a leaf
        int i = 0;
        while ((i = tree[i + Read(probabilities[i >> 1])]) > 0) { }
        // Return leaf
        return -i;
    }
}
