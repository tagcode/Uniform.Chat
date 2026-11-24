namespace Uniform.Matrix.Audio.Decoding.Mp3;

/// <summary>Binary decoding trees of Huffman tables of <see cref="Mp3Tables.HuffmanCodes"/>.</summary>
static class Mp3Huffman
{
    /// <summary>Tree of each table: node n has children at [2n] for bit 0 and [2n + 1] for bit 1. Child &gt; 0 is node index, child &lt;= 0 is leaf of value -child.</summary>
    static readonly int[][] trees = Mp3Tables.HuffmanCodes.Select(BuildTree).ToArray();

    /// <summary>Decode one value with table <paramref name="tableIndex"/> of <see cref="Mp3Tables.HuffmanCodes"/>.</summary>
    /// <returns>Pair table index x * n + y, or count1 value vwxy.</returns>
    public static int Decode(int tableIndex, ref Mp3BitReader reader)
    {
        int[] tree = trees[tableIndex];
        int node = 0;
        // Walk until leaf; complete codes always end in a leaf within 19 bits
        for (int depth = 0; depth < 32; depth++)
        {
            int child = tree[2 * node + reader.ReadBit()];
            if (child <= 0) return -child;
            node = child;
        }
        // On malformed tree
        return 0;
    }

    /// <summary>Build tree of <paramref name="codes"/>, indexed by value, each (length &lt;&lt; 24) | code.</summary>
    static int[] BuildTree(uint[] codes)
    {
        List<int> tree = [0, 0];
        for (int value = 0; value < codes.Length; value++)
        {
            int length = (int)(codes[value] >> 24);
            uint code = codes[value] & 0xFFFFFF;
            int node = 0;
            // Branches down to last bit
            for (int bitIx = length - 1; bitIx > 0; bitIx--)
            {
                int slot = 2 * node + (int)((code >> bitIx) & 1);
                if (tree[slot] <= 0) { tree[slot] = tree.Count / 2; tree.Add(0); tree.Add(0); }
                node = tree[slot];
            }
            // Leaf
            tree[2 * node + (int)(code & 1)] = -value;
        }
        // Return tree
        return tree.ToArray();
    }
}
