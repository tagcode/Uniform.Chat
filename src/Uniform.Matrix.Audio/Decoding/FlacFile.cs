namespace Uniform.Matrix.Audio.Decoding;

/// <summary>Decodes <see href="https://www.rfc-editor.org/rfc/rfc9639">FLAC</see> files in C#: constant, verbatim, fixed and LPC subframes, Rice-coded residuals, and left/side, right/side and mid/side stereo.</summary>
/// <remarks>Frames follow each other; where one does not decode, the next sync code is looked for. Samples of up to 24 bits are supported, and 32 bits without stereo decorrelation. CRCs are not checked.</remarks>
internal static class FlacFile
{
    /// <summary>Sample rates by a frame header's sample rate code 1 to 11.</summary>
    private static readonly int[] SampleRates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    /// <summary>Bits per sample by a frame header's sample size code; 0 for the stream's, -1 for reserved.</summary>
    private static readonly int[] SampleSizes = [0, 8, 12, -1, 16, 20, 24, 32];

    /// <summary>The stream's format, from its STREAMINFO block.</summary>
    private readonly record struct StreamInfo(int SampleRate, int ChannelCount, int BitsPerSample, long TotalFrames, int MaxBlockSize);

    /// <summary>Decode <paramref name="data"/> into <paramref name="clip"/>.</summary>
    /// <exception cref="InvalidDataException">When it is not FLAC or has no STREAMINFO.</exception>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // The marker and metadata blocks
        int position = SkipId3(data);
        if (data.Length < position + 4 || !data.AsSpan(position, 4).SequenceEqual("fLaC"u8)) throw new InvalidDataException("Not a FLAC file.");
        StreamInfo info = ReadMetadata(data, ref position);
        clip.SetFormat(info.SampleRate, info.ChannelCount);

        // Frames until the end, each into floats
        int[][] channels = [.. Enumerable.Range(0, info.ChannelCount).Select(_ => new int[Math.Max(info.MaxBlockSize, 4608)])];
        float[] interleaved = [];
        while (position + 2 <= data.Length)
        {
            if (data[position] != 0xFF || (data[position + 1] & 0xFE) != 0xF8)
            {
                position++;
                continue;
            }
            int frames;
            int bits;
            FlacBitReader reader = new(data, position);
            try { (frames, bits) = DecodeFrame(ref reader, info, ref channels); }
            catch (InvalidDataException)
            {
                position++;
                continue;
            }
            if (interleaved.Length < frames * info.ChannelCount) interleaved = new float[frames * info.ChannelCount];
            float scale = 1f / (1L << (bits - 1));
            for (int frame = 0, i = 0; frame < frames; frame++)
                for (int channel = 0; channel < info.ChannelCount; channel++) interleaved[i++] = channels[channel][frame] * scale;
            clip.Append(interleaved.AsSpan(0, frames * info.ChannelCount));
            position = reader.BytePosition;
        }
        // Done: no longer than the stream tells
        if (info.TotalFrames > 0) clip.KeepFrames(info.TotalFrames);
    }

    /// <summary>Read the metadata blocks after the marker, returning the STREAMINFO, and move <paramref name="position"/> to the first frame.</summary>
    private static StreamInfo ReadMetadata(byte[] data, ref int position)
    {
        // Blocks until the last one
        position += 4;
        StreamInfo? info = null;
        bool last = false;
        while (!last)
        {
            if (position + 4 > data.Length) throw new InvalidDataException("FLAC metadata is cut short.");
            last = (data[position] & 0x80) != 0;
            int type = data[position] & 0x7F;
            int length = data[position + 1] << 16 | data[position + 2] << 8 | data[position + 3];
            if (position + 4 + length > data.Length) throw new InvalidDataException("FLAC metadata is cut short.");
            if (type == 0 && length >= 34)
            {
                FlacBitReader reader = new(data, position + 4);
                reader.ReadBits(16);
                int maxBlock = (int)reader.ReadBits(16);
                reader.ReadBits(24);
                reader.ReadBits(24);
                int rate = (int)reader.ReadBits(20);
                int channelCount = (int)reader.ReadBits(3) + 1;
                int bits = (int)reader.ReadBits(5) + 1;
                long total = (long)reader.ReadBits(4) << 32 | reader.ReadBits(32);
                info = new StreamInfo(rate, channelCount, bits, total, maxBlock);
            }
            position += 4 + length;
        }
        // Return the stream's format
        return info is { SampleRate: > 0 } found ? found : throw new InvalidDataException("FLAC has no STREAMINFO.");
    }

    /// <summary>Decode the frame at the reader into <paramref name="channels"/>; return its block size and bits per sample, and leave the reader after it.</summary>
    private static (int Frames, int Bits) DecodeFrame(ref FlacBitReader reader, StreamInfo info, ref int[][] channels)
    {
        // Header: sync, block size, sample rate, channels, sample size, frame number
        if (reader.ReadBits(15) != 0x7FFC) throw new InvalidDataException("No FLAC frame sync.");
        reader.ReadBits(1);
        int blockCode = (int)reader.ReadBits(4);
        int rateCode = (int)reader.ReadBits(4);
        int assignment = (int)reader.ReadBits(4);
        int sizeCode = (int)reader.ReadBits(3);
        if (reader.ReadBits(1) != 0 || rateCode == 15 || assignment > 10 || SampleSizes[sizeCode] < 0) throw new InvalidDataException("Bad FLAC frame header.");
        ReadUtf8Number(ref reader);
        int blockSize = blockCode switch
        {
            0 => throw new InvalidDataException("Reserved FLAC block size."),
            1 => 192,
            <= 5 => 576 << (blockCode - 2),
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            _ => 256 << (blockCode - 8),
        };
        if (rateCode == 12) reader.ReadBits(8);
        else if (rateCode is 13 or 14) reader.ReadBits(16);
        if (rateCode is > 0 and < 12 && SampleRates[rateCode] != info.SampleRate) throw new InvalidDataException("FLAC frame of another sample rate.");
        reader.ReadBits(8);
        int bits = SampleSizes[sizeCode] == 0 ? info.BitsPerSample : SampleSizes[sizeCode];
        int channelCount = assignment < 8 ? assignment + 1 : 2;
        if (channelCount != info.ChannelCount) throw new InvalidDataException("FLAC frame of another channel count.");
        if (assignment >= 8 && bits > 24) throw new NotSupportedException("FLAC stereo of more than 24 bits is not supported.");
        if (channels[0].Length < blockSize) channels = [.. channels.Select(_ => new int[blockSize])];

        // Subframes, the side channel one bit wider
        for (int channel = 0; channel < channelCount; channel++)
        {
            bool side = assignment == 8 && channel == 1 || assignment == 9 && channel == 0 || assignment == 10 && channel == 1;
            DecodeSubframe(ref reader, channels[channel], blockSize, bits + (side ? 1 : 0));
        }

        // Stereo decorrelation
        int[] first = channels[0], second = channelCount > 1 ? channels[1] : first;
        for (int i = 0; i < blockSize && assignment >= 8; i++)
        {
            switch (assignment)
            {
                case 8:
                    second[i] = first[i] - second[i];
                    break;
                case 9:
                    first[i] += second[i];
                    break;
                default:
                    int mid = first[i] << 1 | (second[i] & 1);
                    first[i] = (mid + second[i]) >> 1;
                    second[i] = (mid - second[i]) >> 1;
                    break;
            }
        }
        // Return after the footer's CRC-16
        reader.AlignToByte();
        reader.ReadBits(16);
        return (blockSize, bits);
    }

    /// <summary>Decode a subframe of <paramref name="blockSize"/> samples of <paramref name="bits"/> bits into <paramref name="samples"/>.</summary>
    private static void DecodeSubframe(ref FlacBitReader reader, int[] samples, int blockSize, int bits)
    {
        // Header: type and wasted bits
        if (reader.ReadBits(1) != 0) throw new InvalidDataException("Bad FLAC subframe header.");
        int type = (int)reader.ReadBits(6);
        int wasted = reader.ReadBits(1) == 1 ? reader.ReadUnary() + 1 : 0;
        bits -= wasted;
        if (bits <= 0) throw new InvalidDataException("Bad FLAC wasted bits.");

        // By type
        switch (type)
        {
            case 0:
                Array.Fill(samples, reader.ReadSigned(bits), 0, blockSize);
                break;
            case 1:
                for (int i = 0; i < blockSize; i++) samples[i] = reader.ReadSigned(bits);
                break;
            case >= 8 and <= 12:
                DecodeFixed(ref reader, samples, blockSize, bits, type - 8);
                break;
            case >= 32:
                DecodeLpc(ref reader, samples, blockSize, bits, type - 31);
                break;
            default:
                throw new InvalidDataException($"Reserved FLAC subframe type {type}.");
        }
        // Done: wasted bits back
        if (wasted > 0) for (int i = 0; i < blockSize; i++) samples[i] <<= wasted;
    }

    /// <summary>Decode a fixed-predictor subframe of <paramref name="order"/> 0 to 4.</summary>
    private static void DecodeFixed(ref FlacBitReader reader, int[] samples, int blockSize, int bits, int order)
    {
        // Warm-up samples, then the residual
        if (order > blockSize) throw new InvalidDataException("FLAC predictor order beyond the block.");
        for (int i = 0; i < order; i++) samples[i] = reader.ReadSigned(bits);
        ReadResidual(ref reader, samples, blockSize, order);
        // Done: each sample predicted from the ones before
        for (int i = order; i < blockSize; i++)
        {
            samples[i] += order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
            };
        }
    }

    /// <summary>Decode a linear-prediction subframe of <paramref name="order"/> 1 to 32.</summary>
    private static void DecodeLpc(ref FlacBitReader reader, int[] samples, int blockSize, int bits, int order)
    {
        // Warm-up samples, coefficients, then the residual
        if (order > blockSize) throw new InvalidDataException("FLAC predictor order beyond the block.");
        for (int i = 0; i < order; i++) samples[i] = reader.ReadSigned(bits);
        int precision = (int)reader.ReadBits(4) + 1;
        if (precision == 16) throw new InvalidDataException("Bad FLAC coefficient precision.");
        int shift = reader.ReadSigned(5);
        if (shift < 0) throw new InvalidDataException("Negative FLAC prediction shift.");
        Span<int> coefficients = stackalloc int[order];
        for (int i = 0; i < order; i++) coefficients[i] = reader.ReadSigned(precision);
        ReadResidual(ref reader, samples, blockSize, order);
        // Done: each sample predicted from the ones before
        for (int i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)coefficients[j] * samples[i - 1 - j];
            samples[i] += (int)(sum >> shift);
        }
    }

    /// <summary>Read a Rice-coded residual into <paramref name="samples"/> after the <paramref name="order"/> warm-up samples.</summary>
    private static void ReadResidual(ref FlacBitReader reader, int[] samples, int blockSize, int order)
    {
        // Coding method and partitions
        int method = (int)reader.ReadBits(2);
        if (method > 1) throw new InvalidDataException("Reserved FLAC residual coding method.");
        int parameterBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int partitionOrder = (int)reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int perPartition = blockSize >> partitionOrder;
        if (perPartition << partitionOrder != blockSize || perPartition < order) throw new InvalidDataException("Bad FLAC residual partitions.");

        // Each partition: Rice parameter, or escaped plain bits
        int index = order;
        for (int partition = 0; partition < partitions; partition++)
        {
            int count = partition == 0 ? perPartition - order : perPartition;
            int parameter = (int)reader.ReadBits(parameterBits);
            if (parameter == escape)
            {
                int plain = (int)reader.ReadBits(5);
                for (int i = 0; i < count; i++) samples[index++] = reader.ReadSigned(plain);
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                uint value = (uint)reader.ReadUnary() << parameter | reader.ReadBits(parameter);
                samples[index++] = (int)(value >> 1) ^ -(int)(value & 1);
            }
        }
    }

    /// <summary>Read the frame or sample number, coded as UTF-8 of up to 7 bytes.</summary>
    private static void ReadUtf8Number(ref FlacBitReader reader)
    {
        // Continuation bytes by the first byte's leading ones
        uint first = reader.ReadBits(8);
        int more = (first & 0x80) == 0 ? 0 : (int)uint.LeadingZeroCount(~first << 24) - 1;
        if (more is < 0 or > 6) throw new InvalidDataException("Bad FLAC frame number.");
        for (int i = 0; i < more; i++)
        {
            if ((reader.ReadBits(8) & 0xC0) != 0x80) throw new InvalidDataException("Bad FLAC frame number.");
        }
    }

    /// <summary>Return the position after an ID3v2 tag at the start of <paramref name="data"/>, or 0.</summary>
    private static int SkipId3(byte[] data)
    {
        // Assert a tag
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;
        int size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        // Return the position after it
        return Math.Min(data.Length, 10 + size);
    }
}

/// <summary>Reads bits most significant first from bytes, through a 64-bit cache; reading past the end throws <see cref="InvalidDataException"/>.</summary>
internal struct FlacBitReader(byte[] data, int position)
{
    private ulong cache;
    private int cacheBits;
    private int next = position;

    /// <summary>The position of the next whole byte not yet read.</summary>
    public readonly int BytePosition => next - cacheBits / 8;

    /// <summary>Read <paramref name="count"/> bits, 0 to 32, as an unsigned number.</summary>
    public uint ReadBits(int count)
    {
        // None, or from the cache
        if (count == 0) return 0;
        Refill(count);
        uint value = (uint)(cache >> (64 - count));
        cache <<= count;
        cacheBits -= count;
        // Return value
        return value;
    }

    /// <summary>Read <paramref name="count"/> bits, 0 to 32, as a two's complement number.</summary>
    public int ReadSigned(int count) => count == 0 ? 0 : (int)(ReadBits(count) << (32 - count)) >> (32 - count);

    /// <summary>Read zeros up to a one, and return how many there were.</summary>
    public int ReadUnary()
    {
        // Leading zeros of the cache, refilled as they run out
        int zeros = 0;
        while (true)
        {
            Refill(1);
            int leading = (int)ulong.LeadingZeroCount(cache);
            if (leading < cacheBits)
            {
                cache <<= leading + 1;
                cacheBits -= leading + 1;
                // Return count
                return zeros + leading;
            }
            zeros += cacheBits;
            cache = 0;
            cacheBits = 0;
        }
    }

    /// <summary>Skip to the next whole byte.</summary>
    public void AlignToByte()
    {
        // Drop the bits of a partly read byte
        int partial = cacheBits % 8;
        cache <<= partial;
        cacheBits -= partial;
    }

    /// <summary>Fill the cache with whole bytes, so it has at least <paramref name="count"/> bits.</summary>
    private void Refill(int count)
    {
        // Bytes while they fit
        while (cacheBits <= 56 && next < data.Length)
        {
            cache |= (ulong)data[next++] << (56 - cacheBits);
            cacheBits += 8;
        }
        // On the end of the data
        if (cacheBits < count) throw new InvalidDataException("FLAC data is cut short.");
    }
}
