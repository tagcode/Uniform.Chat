namespace Uniform.Matrix.Audio.Decoding;

using System.Buffers.Binary;
using Concentus;

/// <summary>Decodes Opus with Concentus in C#, at 48 kHz: <see href="https://www.rfc-editor.org/rfc/rfc7845">Ogg Opus</see> files, as Element's voice messages are, and packets of other containers, such as WebM, with their identification header.</summary>
/// <remarks>The header's pre-skip is dropped and, in Ogg, the end trimmed to the last granule position, so the clip has exactly the encoded samples; the output gain is applied. Mono and stereo streams of channel mapping family 0 are supported.</remarks>
internal static class OpusFile
{
    /// <summary>Opus decodes at 48 kHz.</summary>
    private const int SampleRate = 48000;

    /// <summary>The longest Opus packet, 120 ms, in frames.</summary>
    private const int MaxFrames = SampleRate * 120 / 1000;

    /// <summary>The identification header's fields that decoding needs.</summary>
    private readonly record struct OpusHead(int Channels, int PreSkip, float Gain);

    /// <summary>Decode the Ogg Opus file <paramref name="data"/> into <paramref name="clip"/>.</summary>
    /// <exception cref="InvalidDataException">When it has no Opus header.</exception>
    /// <exception cref="NotSupportedException">When it has more than two channels.</exception>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // The identification header, then the comment header
        using IEnumerator<OggPacket> packets = OggPackets.Read(data).GetEnumerator();
        if (!packets.MoveNext()) throw new InvalidDataException("No Opus header found.");
        OpusHead head = ReadHead(packets.Current.Data);
        packets.MoveNext();

        // Each audio packet, noting the last granule
        long lastGranule = -1;
        DecodePackets(head, Remaining(packets, granule => lastGranule = granule), clip);
        // Done: no longer than the last granule tells
        if (lastGranule > head.PreSkip) clip.KeepFrames(lastGranule - head.PreSkip);
    }

    /// <summary>Decode packets of another container into <paramref name="clip"/>, after their identification header <paramref name="head"/>, e.g. a WebM track's codec private data.</summary>
    public static void DecodePackets(byte[] head, IEnumerable<ReadOnlyMemory<byte>> packets, ClipBuilder clip) => DecodePackets(ReadHead(head), packets, clip);

    /// <summary>Return the fields of an <c>OpusHead</c> packet.</summary>
    private static OpusHead ReadHead(byte[] head)
    {
        // Magic, channels, pre-skip, gain, mapping family
        if (head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)) throw new InvalidDataException("No Opus header found.");
        int channels = head[9];
        if (head[18] != 0 || channels is < 1 or > 2) throw new NotSupportedException($"Opus with {channels} channels and mapping family {head[18]} is not supported.");
        // Return the fields
        return new OpusHead(channels, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)), MathF.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)) / (20f * 256)));
    }

    /// <summary>Decode audio packets into <paramref name="clip"/>, without the pre-skip, with the gain.</summary>
    private static void DecodePackets(OpusHead head, IEnumerable<ReadOnlyMemory<byte>> packets, ClipBuilder clip)
    {
        // Format, and the frames to skip
        clip.SetFormat(SampleRate, head.Channels);
        clip.SkipFrames(head.PreSkip);
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, head.Channels);
        float[] pcm = new float[MaxFrames * head.Channels];

        // Done: each packet
        foreach (ReadOnlyMemory<byte> packet in packets)
        {
            if (packet.IsEmpty) continue;
            int frames = decoder.Decode(packet.Span, pcm, MaxFrames, false);
            if (frames <= 0) continue;
            if (head.Gain != 1) for (int i = 0; i < frames * head.Channels; i++) pcm[i] *= head.Gain;
            clip.Append(pcm.AsSpan(0, frames * head.Channels));
        }
    }

    /// <summary>Return the remaining Ogg packets' data, telling each page granule seen.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> Remaining(IEnumerator<OggPacket> packets, Action<long> granule)
    {
        // Each one
        while (packets.MoveNext())
        {
            if (packets.Current.Granule >= 0) granule(packets.Current.Granule);
            yield return packets.Current.Data;
        }
    }
}
