namespace Uniform.Matrix.WebRtc.Stun;

/// <summary>CRC-32 as ISO 3309 and ITU-T V.42 define it, the one zlib and Ethernet use, for STUN's FINGERPRINT.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Return the CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        // Table-driven, reflected
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        // Return the final XOR
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Return the table of the reflected polynomial 0xEDB88320.</summary>
    private static uint[] BuildTable()
    {
        // One entry per byte value
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        // Return table
        return table;
    }
}
