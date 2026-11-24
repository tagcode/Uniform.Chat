namespace Uniform.Matrix.Audio.Decoding.Mp3;

/// <summary>Header of an MPEG audio frame, the 4 bytes that start each frame.</summary>
/// <see href="http://www.mp3-tech.org/programmer/frame_header.html"/>
readonly record struct Mp3FrameHeader
{
    /// <summary>Layer III bitrates in kbit/s by bitrate index, MPEG-1.</summary>
    static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    /// <summary>Layer III bitrates in kbit/s by bitrate index, MPEG-2 and MPEG-2.5.</summary>
    static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    /// <summary>MPEG-1 sample rates by sample rate index.</summary>
    static readonly int[] Mpeg1SampleRates = [44100, 48000, 32000];

    /// <summary>Version: 1 for MPEG-1, 2 for MPEG-2, 25 for MPEG-2.5.</summary>
    public int Version { get; init; }
    /// <summary>Layer 1, 2 or 3.</summary>
    public int Layer { get; init; }
    /// <summary>'true' if 16 bit CRC follows header.</summary>
    public bool HasCrc { get; init; }
    /// <summary>Bitrate in kbit/s.</summary>
    public int Bitrate { get; init; }
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    /// <summary>Sample rate index 0..8 of <see cref="Mp3Tables.LongBands"/>.</summary>
    public int SampleRateTableIndex { get; init; }
    /// <summary>'true' if frame has one padding byte.</summary>
    public bool Padding { get; init; }
    /// <summary>Channel mode: 0 stereo, 1 joint stereo, 2 dual channel, 3 mono.</summary>
    public int Mode { get; init; }
    /// <summary>Mode extension of joint stereo: bit 1 mid/side stereo, bit 0 intensity stereo.</summary>
    public int ModeExtension { get; init; }

    /// <summary>'true' for MPEG-1, which has 2 granules per frame; MPEG-2 and 2.5 have 1.</summary>
    public bool IsMpeg1 => Version == 1;
    /// <summary>Number of channels, 1 or 2.</summary>
    public int ChannelCount => Mode == 3 ? 1 : 2;
    /// <summary>Number of granules of 576 samples, 2 for MPEG-1, 1 otherwise.</summary>
    public int GranuleCount => IsMpeg1 ? 2 : 1;
    /// <summary>Samples per channel in frame.</summary>
    public int SampleCount => GranuleCount * 576;
    /// <summary>Frame length in bytes, including header.</summary>
    public int FrameLength => (IsMpeg1 ? 144000 : 72000) * Bitrate / SampleRate + (Padding ? 1 : 0);
    /// <summary>Length of side information in bytes.</summary>
    public int SideInfoLength => IsMpeg1 ? (ChannelCount == 1 ? 17 : 32) : (ChannelCount == 1 ? 9 : 17);
    /// <summary>Offset of side information from frame start.</summary>
    public int SideInfoOffset => HasCrc ? 6 : 4;

    /// <summary>Parse header of any layer at <paramref name="offset"/> of <paramref name="data"/>.</summary>
    /// <returns>'false' if there is no valid header, or bitrate is free format.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, int offset, out Mp3FrameHeader header)
    {
        // Assert sync
        header = default;
        if (offset < 0 || offset + 4 > data.Length) return false;
        byte b1 = data[offset + 1], b2 = data[offset + 2], b3 = data[offset + 3];
        if (data[offset] != 0xFF || (b1 & 0xE0) != 0xE0) return false;
        // Version, layer, bitrate and sample rate
        int versionBits = (b1 >> 3) & 3, layerBits = (b1 >> 1) & 3, bitrateIndex = b2 >> 4, sampleRateIndex = (b2 >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3) return false;
        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int sampleRate = Mpeg1SampleRates[sampleRateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
        int tableIndex = (version == 1 ? 0 : version == 2 ? 3 : 6) + sampleRateIndex;
        header = new Mp3FrameHeader
        {
            Version = version,
            Layer = 4 - layerBits,
            HasCrc = (b1 & 1) == 0,
            Bitrate = (version == 1 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex],
            SampleRate = sampleRate,
            SampleRateTableIndex = tableIndex,
            Padding = ((b2 >> 1) & 1) != 0,
            Mode = b3 >> 6,
            ModeExtension = (b3 >> 4) & 3,
        };
        // Done
        return true;
    }

    /// <summary>Test whether <paramref name="other"/> belongs to the same stream: same version, layer, sample rate and channel count.</summary>
    public bool IsCompatible(Mp3FrameHeader other) => other.Version == Version && other.Layer == Layer && other.SampleRate == SampleRate && other.ChannelCount == ChannelCount;
}
