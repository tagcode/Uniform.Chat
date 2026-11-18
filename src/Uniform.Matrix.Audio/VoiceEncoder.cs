namespace Uniform.Matrix.Audio;

using Concentus;
using Concentus.Enums;

/// <summary>Encodes 20 ms voice frames as Opus packets, with Concentus in pure C#.</summary>
/// <remarks>Tuned for speech: the VoIP application, voice signal, variable bit rate and in-band forward error correction, so a lost packet can be partly recovered from the next. At 64 kbit/s Opus codes full-band speech, up to 20 kHz, where 32 kbit/s gives wideband.</remarks>
public sealed class VoiceEncoder
{
    /// <summary>Largest Opus packet.</summary>
    public const int MaxPacketBytes = 1275;

    /// <summary>The bit rate calls send at, in bits per second.</summary>
    public const int DefaultBitrate = 64000;
    private readonly IOpusEncoder encoder;

    /// <summary>Create an encoder.</summary>
    /// <param name="bitrate">Bits per second, at most; 64 kbit/s gives full-band speech, 32 kbit/s clear wideband speech.</param>
    public VoiceEncoder(int bitrate = DefaultBitrate)
    {
        // Pure C# on every platform, whether or not a native libopus is installed
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SampleRate, VoiceFormat.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrate;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.UseInbandFEC = true;
        encoder.PacketLossPercent = 10;
    }

    /// <summary>Encode one frame of <see cref="VoiceFormat.FrameSamples"/> samples; return the Opus packet.</summary>
    public byte[] Encode(ReadOnlySpan<short> frame)
    {
        // Assert a whole frame
        if (frame.Length != VoiceFormat.FrameSamples) throw new ArgumentException($"A frame has {VoiceFormat.FrameSamples} samples, not {frame.Length}.", nameof(frame));
        Span<byte> packet = stackalloc byte[MaxPacketBytes];
        int length = encoder.Encode(frame, VoiceFormat.FrameSamples, packet, packet.Length);
        // Return the packet
        return packet[..length].ToArray();
    }
}
