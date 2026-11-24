namespace Uniform.Matrix.Audio.Decoding;

using System.Runtime.InteropServices;
using Uniform.Matrix.Audio.Decoding.Mp3;

/// <summary>Decodes MP3 files, MPEG-1, MPEG-2 and MPEG-2.5 audio layer III, in C# without dependencies.</summary>
/// <remarks>
/// ID3v2 tags at the start are skipped, and frames are found by their sync word, checked by the header that follows. A first Xing or Info frame with a LAME header gives the encoder's delay and padding, which are trimmed, so the clip has exactly the encoded samples. Layer I and II, and free-format bitrates, are not supported.
/// Copied from Uniform.Audio.Sampling.Encoding of Uniform.Audio.Music, by the same author.
/// </remarks>
/// <see href="https://www.iso.org/standard/22412.html">ISO/IEC 11172-3</see>
/// <see href="http://gabriel.mp3-tech.org/mp3infotag.html">LAME info tag</see>
internal static class Mp3File
{
    /// <summary>Delay of the decoder's output in samples, from the synthesis filter bank and the IMDCT overlap.</summary>
    private const int DecoderDelay = 529;

    /// <summary>Decode <paramref name="data"/> into <paramref name="clip"/>.</summary>
    /// <exception cref="InvalidDataException">When it has no MPEG audio frames.</exception>
    /// <exception cref="NotSupportedException">When it is layer I or II.</exception>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // The first frame, after an ID3v2 tag
        int position = SkipId3(data);
        if (!TryFindFrame(data, ref position, null, out Mp3FrameHeader first)) throw new InvalidDataException("No MPEG audio frame found.");
        if (first.Layer != 3) throw new NotSupportedException($"MPEG audio layer {first.Layer} is not supported, only layer III.");
        clip.SetFormat(first.SampleRate, first.ChannelCount);

        // Gapless info of a Xing or Info frame, which has no audio
        int padding = 0;
        if (TryReadLameGapless(data.AsSpan(position, Math.Min(first.FrameLength, data.Length - position)), first, out int encoderDelay, out int encoderPadding, out bool isInfoFrame))
        {
            clip.SkipFrames(encoderDelay + DecoderDelay);
            padding = Math.Max(0, encoderPadding - DecoderDelay);
        }
        if (isInfoFrame) position += first.FrameLength;

        // Each frame, through a frame's worth of floats
        Mp3LayerIIIDecoder decoder = new();
        List<float> frame = [];
        while (TryFindFrame(data, ref position, first, out Mp3FrameHeader header))
        {
            if (position + header.FrameLength > data.Length) break;
            decoder.DecodeFrame(data.AsSpan(position, header.FrameLength), header, frame);
            clip.Append(CollectionsMarshal.AsSpan(frame));
            frame.Clear();
            position += header.FrameLength;
        }
        // Done: without the padding
        clip.TrimEnd(padding);
    }

    /// <summary>Return the position after an ID3v2 tag at the start of <paramref name="data"/>, or 0.</summary>
    private static int SkipId3(byte[] data)
    {
        // Assert a tag
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;
        // Synchsafe size, and footer
        int size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        int footer = (data[5] & 0x10) != 0 ? 10 : 0;
        // Return the position after the tag
        return Math.Min(data.Length, 10 + size + footer);
    }

    /// <summary>Find the next frame from <paramref name="position"/>: one like <paramref name="reference"/> is taken as is; otherwise the next header must follow at the frame's end.</summary>
    private static bool TryFindFrame(byte[] data, ref int position, Mp3FrameHeader? reference, out Mp3FrameHeader header)
    {
        // Each position, until a header
        for (; position + 4 <= data.Length; position++)
        {
            if (!Mp3FrameHeader.TryParse(data, position, out header)) continue;
            if (reference is Mp3FrameHeader known && !known.IsCompatible(header)) continue;
            if (reference != null) return true;
            int next = position + header.FrameLength;
            if (next + 4 > data.Length) return true;
            if (Mp3FrameHeader.TryParse(data, next, out Mp3FrameHeader following) && following.IsCompatible(header)) return true;
        }
        // On the end of the data
        header = default;
        return false;
    }

    /// <summary>Read the encoder's delay and padding from the LAME header of a Xing or Info <paramref name="frame"/>; return whether it has them.</summary>
    /// <param name="frame">The whole first frame.</param>
    /// <param name="header">Its header.</param>
    /// <param name="delay">Encoder delay in samples.</param>
    /// <param name="padding">Encoder padding at the end in samples.</param>
    /// <param name="isInfoFrame">Whether the frame is a Xing or Info frame, which carries no audio.</param>
    private static bool TryReadLameGapless(ReadOnlySpan<byte> frame, Mp3FrameHeader header, out int delay, out int padding, out bool isInfoFrame)
    {
        // Xing or Info tag after the side information
        delay = padding = 0;
        int offset = header.SideInfoOffset + header.SideInfoLength;
        isInfoFrame = frame.Length >= offset + 8 && (frame.Slice(offset, 4).SequenceEqual("Xing"u8) || frame.Slice(offset, 4).SequenceEqual("Info"u8));
        if (!isInfoFrame) return false;
        // Optional fields: frames, bytes, table of contents, quality
        int flags = frame[offset + 7];
        int lame = offset + 8 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
        if (lame + 24 > frame.Length) return false;
        // Encoder name, e.g. "LAME3.100" or "Lavc60.31", then delay and padding of 12 bits each at offset 21
        if (!char.IsLetter((char)frame[lame]) || !char.IsLetter((char)frame[lame + 1]) || !char.IsLetter((char)frame[lame + 2])) return false;
        delay = frame[lame + 21] << 4 | frame[lame + 22] >> 4;
        padding = (frame[lame + 22] & 0x0F) << 8 | frame[lame + 23];
        // Done
        return true;
    }
}
