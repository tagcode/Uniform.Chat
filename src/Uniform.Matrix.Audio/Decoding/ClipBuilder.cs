namespace Uniform.Matrix.Audio.Decoding;

/// <summary>Collects decoded float samples into an <see cref="AudioClip"/>: as 16-bit, mixed down to stereo, with frames skipped at the start and trimmed at the end for gapless formats, and at most a number of samples, so a long file does not take all memory.</summary>
internal sealed class ClipBuilder(long maxSamples)
{
    private short[] samples = new short[4096];
    private int count;
    private int sourceChannels;
    private long skipFrames;

    /// <summary>Frames per second of the clip.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Channels of the clip, 1 or 2.</summary>
    public int ChannelCount { get; private set; }

    /// <summary>Frames collected so far.</summary>
    public int FrameCount => ChannelCount == 0 ? 0 : count / ChannelCount;

    /// <summary>Set the format of the samples that follow: <paramref name="channelCount"/> interleaved channels of the source, mixed to two when more.</summary>
    public void SetFormat(int sampleRate, int channelCount)
    {
        // Assert a format
        if (sampleRate <= 0 || channelCount <= 0) throw new InvalidDataException($"Bad audio format: {sampleRate} Hz, {channelCount} channels.");
        SampleRate = sampleRate;
        sourceChannels = channelCount;
        // Done
        ChannelCount = Math.Min(channelCount, 2);
    }

    /// <summary>Drop the next <paramref name="frames"/> frames appended, e.g. a codec's delay at the start.</summary>
    public void SkipFrames(long frames) => skipFrames += Math.Max(0, frames);

    /// <summary>Append interleaved samples of the source format, nominally -1 to 1.</summary>
    /// <exception cref="NotSupportedException">When the clip would exceed its maximum, as a file too long to play.</exception>
    public void Append(ReadOnlySpan<float> interleaved)
    {
        // Whole frames, after the ones to skip
        int frames = interleaved.Length / sourceChannels;
        int skipped = (int)Math.Min(skipFrames, frames);
        skipFrames -= skipped;
        if (skipped == frames) return;
        int added = (frames - skipped) * ChannelCount;
        if (count + (long)added > maxSamples) throw new NotSupportedException("The audio is too long to play here.");
        if (count + added > samples.Length) Array.Resize(ref samples, (int)Math.Min(maxSamples, Math.Max((long)samples.Length * 2, count + added)));

        // Each frame, mixed down when more than two channels: even channels left, odd right
        for (int frame = skipped; frame < frames; frame++)
        {
            ReadOnlySpan<float> source = interleaved.Slice(frame * sourceChannels, sourceChannels);
            if (sourceChannels <= 2)
            {
                foreach (float value in source) samples[count++] = ToShort(value);
                continue;
            }
            float left = 0, right = 0;
            for (int channel = 0; channel < sourceChannels; channel++)
            {
                if (channel % 2 == 0) left += source[channel];
                else right += source[channel];
            }
            samples[count++] = ToShort(left / ((sourceChannels + 1) / 2));
            samples[count++] = ToShort(right / (sourceChannels / 2));
        }
    }

    /// <summary>Remove the last <paramref name="frames"/> frames, e.g. a codec's padding at the end.</summary>
    public void TrimEnd(long frames) => count = Math.Max(0, count - (int)Math.Min(FrameCount, Math.Max(0, frames)) * ChannelCount);

    /// <summary>Keep at most <paramref name="frames"/> frames, e.g. the length a container tells.</summary>
    public void KeepFrames(long frames) => count = (int)Math.Min(count, Math.Max(0, frames) * ChannelCount);

    /// <summary>Return the clip collected.</summary>
    /// <exception cref="InvalidDataException">When nothing was decoded.</exception>
    public AudioClip Build()
    {
        // Assert audio
        if (ChannelCount == 0 || count == 0) throw new InvalidDataException("The file has no audio.");
        // Return clip
        return new AudioClip(samples[..count], SampleRate, ChannelCount);
    }

    /// <summary>Return a sample as 16-bit, clipped.</summary>
    private static short ToShort(float value) => (short)Math.Clamp(MathF.Round(value * 32767f), short.MinValue, short.MaxValue);
}
