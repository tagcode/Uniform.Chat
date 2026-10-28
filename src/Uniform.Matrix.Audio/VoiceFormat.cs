namespace Uniform.Matrix.Audio;

using NAudio.Wave;

/// <summary>The audio of voice calls: 48 kHz mono 16-bit samples in 20 ms frames, as <see href="https://www.rfc-editor.org/rfc/rfc7587">Opus over RTP</see> usually carries them.</summary>
public static class VoiceFormat
{
    /// <summary>Samples per second.</summary>
    public const int SampleRate = 48000;
    /// <summary>Channels; voice is mono.</summary>
    public const int Channels = 1;
    /// <summary>Length of one frame in milliseconds.</summary>
    public const int FrameMilliseconds = 20;
    /// <summary>Samples in one frame.</summary>
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000;

    /// <summary>The format as NAudio describes it, 16-bit PCM.</summary>
    public static WaveFormat WaveFormat { get; } = new(SampleRate, 16, Channels);
}
