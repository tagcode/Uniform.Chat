namespace Uniform.Matrix.Audio;

using Concentus;

/// <summary>Decodes Opus packets into 20 ms voice frames; a missing packet is concealed from the ones before it.</summary>
public sealed class VoiceDecoder
{
    private readonly IOpusDecoder decoder;

    /// <summary>Create a decoder.</summary>
    public VoiceDecoder()
    {
        // Pure C#, as the encoder
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SampleRate, VoiceFormat.Channels);
    }

    /// <summary>Decode a packet into a frame of <see cref="VoiceFormat.FrameSamples"/> samples; null for a packet that never arrived, which is concealed.</summary>
    public short[] Decode(byte[]? packet)
    {
        // Packet loss concealment when there is none
        short[] frame = new short[VoiceFormat.FrameSamples];
        decoder.Decode(packet ?? ReadOnlySpan<byte>.Empty, frame, VoiceFormat.FrameSamples, false);
        // Return frame
        return frame;
    }
}
