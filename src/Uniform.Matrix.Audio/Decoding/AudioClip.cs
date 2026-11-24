namespace Uniform.Matrix.Audio.Decoding;

/// <summary>Decoded audio to play: 16-bit samples, mono or stereo, interleaved, at the file's own sample rate.</summary>
/// <param name="Samples">Interleaved samples, <see cref="ChannelCount"/> per frame.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="ChannelCount">1 or 2.</param>
public sealed record AudioClip(short[] Samples, int SampleRate, int ChannelCount)
{
    /// <summary>Number of frames, samples per channel.</summary>
    public int FrameCount => Samples.Length / ChannelCount;

    /// <summary>How long it plays.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);
}
