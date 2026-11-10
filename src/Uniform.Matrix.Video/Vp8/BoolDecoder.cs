namespace Uniform.Matrix.Video.Vp8;

using System.Numerics;

/// <summary>The boolean entropy decoder of VP8, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-7">RFC 6386 section 7</see>: reads bools, each with a probability of being 0 out of 256, from one partition.</summary>
/// <remarks>Past the end of the partition it reads zeros, as the reference decoder does.</remarks>
internal sealed class BoolDecoder
{
    /// <summary>Bits of the value window.</summary>
    private const int WindowBits = 64;
    private byte[] data = [];
    private int position;
    private int end;
    private uint range;
    private ulong value;
    private int count;

    /// <summary>Start reading <paramref name="length"/> bytes of <paramref name="buffer"/> at <paramref name="start"/>.</summary>
    public void Init(byte[] buffer, int start, int length)
    {
        // An empty window, filled at once
        data = buffer;
        position = start;
        end = start + Math.Max(0, length);
        value = 0;
        count = -8;
        range = 255;
        // Done
        Fill();
    }

    /// <summary>Return a bool that is 0 with <paramref name="probability"/> out of 256.</summary>
    /// <remarks>As libvpx does: the next bits wait in a 64-bit window, and the range is normalized in one shift by its leading zeros rather than bit by bit.</remarks>
    public int Read(int probability)
    {
        // Split the range by the probability, against the window's top byte
        uint split = 1 + ((range - 1) * (uint)probability >> 8);
        if (count < 0) Fill();
        ulong bigSplit = (ulong)split << (WindowBits - 8);
        int bit;
        if (value >= bigSplit)
        {
            range -= split;
            value -= bigSplit;
            bit = 1;
        }
        else
        {
            range = split;
            bit = 0;
        }

        // Normalize the range to at least 128 at once
        int shift = BitOperations.LeadingZeroCount(range) - 24;
        range <<= shift;
        value <<= shift;
        count -= shift;
        // Return bool
        return bit;
    }

    /// <summary>Take in whole bytes while the window has room; past the end, zeros, as the reference decoder reads.</summary>
    private void Fill()
    {
        // From the window's free top bits down
        int shift = WindowBits - 8 - (count + 8);
        while (shift >= 0)
        {
            if (position >= end)
            {
                count += 0x4000;
                return;
            }
            count += 8;
            value |= (ulong)data[position++] << shift;
            shift -= 8;
        }
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
