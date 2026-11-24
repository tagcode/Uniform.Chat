namespace Uniform.Matrix.Audio.Decoding.Mp3;

/// <summary>Read bits most significant first from a byte array. Bits past the end read as 0.</summary>
struct Mp3BitReader(byte[] data, int bitPosition)
{
    /// <summary>Position in bits from start of data.</summary>
    public int Position = bitPosition;

    /// <summary>Read one bit.</summary>
    public int ReadBit()
    {
        int byteIx = Position >> 3, bit = byteIx < data.Length ? (data[byteIx] >> (7 - (Position & 7))) & 1 : 0;
        Position++;
        // Return bit
        return bit;
    }

    /// <summary>Read <paramref name="count"/> bits, 0..32, as unsigned value.</summary>
    public int ReadBits(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++) value = (value << 1) | ReadBit();
        // Return value
        return value;
    }
}
