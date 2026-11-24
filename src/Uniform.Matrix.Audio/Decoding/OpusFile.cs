namespace Uniform.Matrix.Audio.Decoding;

using System.Buffers.Binary;
using Concentus;

/// <summary>Decodes <see href="https://www.rfc-editor.org/rfc/rfc7845">Ogg Opus</see> files, as Element's voice messages are, with Concentus in C#, at 48 kHz.</summary>
/// <remarks>The header's pre-skip is dropped and the end trimmed to the last granule position, so the clip has exactly the encoded samples; the output gain is applied. Mono and stereo streams of channel mapping family 0 are supported.</remarks>
internal static class OpusFile
{
    /// <summary>Opus decodes at 48 kHz.</summary>
    private const int SampleRate = 48000;

    /// <summary>The longest Opus packet, 120 ms, in frames.</summary>
    private const int MaxFrames = SampleRate * 120 / 1000;

    /// <summary>Decode <paramref name="data"/> into <paramref name="clip"/>.</summary>
    /// <exception cref="InvalidDataException">When it has no Opus header.</exception>
    /// <exception cref="NotSupportedException">When it has more than two channels.</exception>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // The identification header
        using IEnumerator<OggPacket> packets = OggPackets.Read(data).GetEnumerator();
        if (!packets.MoveNext() || packets.Current.Data is not { Length: >= 19 } head || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)) throw new InvalidDataException("No Opus header found.");
        int channels = head[9];
        int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        float gain = MathF.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)) / (20f * 256));
        if (head[18] != 0 || channels is < 1 or > 2) throw new NotSupportedException($"Opus with {channels} channels and mapping family {head[18]} is not supported.");
        clip.SetFormat(SampleRate, channels);
        clip.SkipFrames(preSkip);

        // The comment header, then each audio packet
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, channels);
        float[] pcm = new float[MaxFrames * channels];
        long lastGranule = -1;
        packets.MoveNext();
        while (packets.MoveNext())
        {
            OggPacket packet = packets.Current;
            if (packet.Granule >= 0) lastGranule = packet.Granule;
            if (packet.Data.Length == 0) continue;
            int frames = decoder.Decode(packet.Data, pcm, MaxFrames, false);
            if (frames <= 0) continue;
            if (gain != 1) for (int i = 0; i < frames * channels; i++) pcm[i] *= gain;
            clip.Append(pcm.AsSpan(0, frames * channels));
        }
        // Done: no longer than the last granule tells
        if (lastGranule > preSkip) clip.KeepFrames(lastGranule - preSkip);
    }
}
