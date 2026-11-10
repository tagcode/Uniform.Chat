namespace Uniform.Matrix.Video.Vp8;

/// <summary>The boolean entropy encoder of VP8, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-7.3">RFC 6386 section 7.3</see>: writes bools, each with a probability of being 0 out of 256, into one partition, which <see cref="BoolDecoder"/> reads back.</summary>
internal sealed class BoolEncoder
{
    private readonly List<byte> output = [];
    private uint range = 255;
    private uint bottom;
    private int bitCount = 24;

    /// <summary>Write <paramref name="bit"/>, which is 0 with <paramref name="probability"/> out of 256.</summary>
    public void Write(int probability, int bit)
    {
        // Narrow the range to the bit's part
        uint split = 1 + ((range - 1) * (uint)probability >> 8);
        if (bit != 0)
        {
            bottom += split;
            range -= split;
        }
        else range = split;

        // Shift out whole bytes, carrying into those written
        while (range < 128)
        {
            range <<= 1;
            if ((bottom & 1u << 31) != 0) Carry();
            bottom <<= 1;
            if (--bitCount != 0) continue;
            output.Add((byte)(bottom >> 24));
            bottom &= (1 << 24) - 1;
            bitCount = 8;
        }
    }

    /// <summary>Write <paramref name="bits"/> bits of <paramref name="value"/>, most significant first, each even.</summary>
    public void WriteLiteral(int value, int bits)
    {
        // High bit first
        for (int bit = bits - 1; bit >= 0; bit--) Write(128, value >> bit & 1);
    }

    /// <summary>Write the path to <paramref name="value"/> in a <paramref name="tree"/> of the decoder's form, with <paramref name="probabilities"/> of its nodes.</summary>
    public void WriteTree(ReadOnlySpan<int> tree, ReadOnlySpan<byte> probabilities, int value)
    {
        // The bits from the root, found from the leaf
        Span<int> path = stackalloc int[16];
        int length = 0;
        if (!Find(tree, 0, -value, path, ref length)) throw new ArgumentOutOfRangeException(nameof(value));
        // Done
        for (int i = 0, node = 0; i < length; i++)
        {
            Write(probabilities[node >> 1], path[i]);
            node = tree[node + path[i]];
        }
    }

    /// <summary>Flush the last bits, as libvpx does, and return the partition.</summary>
    public byte[] Finish()
    {
        // 32 even zeros push everything out
        for (int i = 0; i < 32; i++) Write(128, 0);
        // Return bytes
        return [.. output];
    }

    /// <summary>Add one to the bytes written, past a run of 255s.</summary>
    private void Carry()
    {
        // Back over full bytes
        int at = output.Count - 1;
        while (at >= 0 && output[at] == 255) output[at--] = 0;
        // Done
        if (at >= 0) output[at]++;
    }

    /// <summary>Find the bits from <paramref name="node"/> to the leaf <paramref name="leaf"/>, a leaf entry being the negated value.</summary>
    private static bool Find(ReadOnlySpan<int> tree, int node, int leaf, Span<int> path, ref int length)
    {
        // Each branch of the node
        for (int bit = 0; bit < 2; bit++)
        {
            int next = tree[node + bit];
            path[length++] = bit;
            if (next <= 0 ? next == leaf : Find(tree, next, leaf, path, ref length)) return true;
            length--;
        }
        // On not here
        return false;
    }
}
