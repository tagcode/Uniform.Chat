namespace Uniform.Matrix.Audio.Tests;

using NAudio.Wave;
using Uniform.Matrix.Audio.Decoding;

/// <summary>Tests of <see cref="AudioFile"/>: each format decodes like a reference decoder, and broken or unsupported files fail cleanly.</summary>
public class AudioFileTests
{
    /// <summary>Folder of the test files, beside the test assembly.</summary>
    private static string Resources => Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>MP3 of MPEG-1, 2 and 2.5, mono and stereo, decodes as mpg123 does, within a 16-bit step.</summary>
    [Theory]
    [InlineData("mpeg1_44k_joint_vbr", 44100, 2)]
    [InlineData("mpeg1_48k_stereo_192", 48000, 2)]
    [InlineData("mpeg1_32k_mono_96", 32000, 1)]
    [InlineData("mpeg2_24k_joint_48", 24000, 2)]
    [InlineData("mpeg2_22k_mono_vbr", 22050, 1)]
    [InlineData("mpeg25_8k_joint_16", 8000, 2)]
    [InlineData("mpeg25_11k_mono_24", 11025, 1)]
    [InlineData("sine440_44k_stereo_128", 44100, 2)]
    public void DecodeMp3LikeMpg123(string name, int sampleRate, int channelCount)
    {
        // Act
        AudioClip clip = Decode(name + ".mp3");
        float[] reference = Reference(name + ".mpg123.wav", out int referenceRate, out int referenceChannels);

        // Assert
        Assert.Equal((sampleRate, channelCount), (clip.SampleRate, clip.ChannelCount));
        Assert.Equal((referenceRate, referenceChannels), (clip.SampleRate, clip.ChannelCount));
        Assert.Equal(reference.Length, clip.Samples.Length);
        for (int i = 0; i < reference.Length; i++) Assert.True(Math.Abs(clip.Samples[i] / 32767f - reference[i]) < 2.5f / 32768, $"Sample {i}: {clip.Samples[i]} vs {reference[i] * 32768}");
    }

    /// <summary>MP3 with a LAME header decodes to exactly the encoded samples, without the encoder's delay and padding.</summary>
    [Fact]
    public void DecodeMp3Gapless()
    {
        // Act
        AudioClip clip = Decode("sine440_44k_stereo_128.mp3");

        // Assert: 0.5 s, starting with the signal
        Assert.Equal(22050, clip.FrameCount);
        Assert.InRange(Math.Abs((int)clip.Samples[0]), 0, 0.05 * 32767);
    }

    /// <summary>FLAC of fixed and LPC predictors, stereo decorrelation and 24-bit mono decodes to exactly the source's samples.</summary>
    [Theory]
    [InlineData("tone_44k_stereo_l0.flac", "tone_44k_stereo.wav")]
    [InlineData("tone_44k_stereo_l12.flac", "tone_44k_stereo.wav")]
    [InlineData("tone_48k_mono_24bit.flac", "tone_48k_mono_24bit.wav")]
    public void DecodeFlacLossless(string flac, string source)
    {
        // Act
        AudioClip decoded = Decode(flac);
        AudioClip original = Decode(source);

        // Assert
        Assert.Equal((original.SampleRate, original.ChannelCount), (decoded.SampleRate, decoded.ChannelCount));
        Assert.Equal(original.Samples, decoded.Samples);
    }

    /// <summary>Ogg Opus, stereo music and mono voice as Element sends, and Ogg Vorbis decode as libopus and libvorbis do, with exactly the encoded length after the pre-skip and end trimming; ffmpeg's libvorbis output ends 128 frames early, so the overlap is compared.</summary>
    [Theory]
    [InlineData("tone_48k_stereo.opus", 48000, 2, 24000)]
    [InlineData("tone_voice_mono.ogg", 48000, 1, 57600)]
    [InlineData("tone_44k_stereo.ogg", 44100, 2, 22050)]
    public void DecodeOggLikeReference(string name, int sampleRate, int channelCount, int frameCount)
    {
        // Act
        AudioClip clip = Decode(name);
        float[] reference = Reference(name + ".ffmpeg.wav", out _, out _);

        // Assert: format, length, and the signal
        Assert.Equal((sampleRate, channelCount, frameCount), (clip.SampleRate, clip.ChannelCount, clip.FrameCount));
        double noise = 0, signal = 0;
        for (int i = 0; i < Math.Min(reference.Length, clip.Samples.Length); i++)
        {
            signal += reference[i] * (double)reference[i];
            double difference = clip.Samples[i] / 32767.0 - reference[i];
            noise += difference * difference;
        }
        double snr = 10 * Math.Log10(signal / Math.Max(noise, 1e-12));
        Assert.True(snr > 60, $"SNR {snr:F1} dB");
    }

    /// <summary>Formats are recognized by content, and playable files by type or name before download.</summary>
    [Fact]
    public void RecognizeFormats()
    {
        // By content
        Assert.Equal(AudioFormat.Wav, AudioFile.Detect(Read("tone_44k_stereo.wav")));
        Assert.Equal(AudioFormat.Mp3, AudioFile.Detect(Read("mpeg1_32k_mono_96.mp3")));
        Assert.Equal(AudioFormat.Flac, AudioFile.Detect(Read("tone_44k_stereo_l0.flac")));
        Assert.Equal(AudioFormat.OggOpus, AudioFile.Detect(Read("tone_voice_mono.ogg")));
        Assert.Equal(AudioFormat.OggVorbis, AudioFile.Detect(Read("tone_44k_stereo.ogg")));
        Assert.Equal(AudioFormat.Unknown, AudioFile.Detect("\0\0\0 ftypM4A "u8));

        // Done: by type or name
        Assert.True(AudioFile.IsPlayable("audio/ogg", null));
        Assert.True(AudioFile.IsPlayable("audio/mpeg; charset=binary", null));
        Assert.True(AudioFile.IsPlayable(null, "song.FLAC"));
        Assert.False(AudioFile.IsPlayable("audio/mp4", "voice.m4a"));
    }

    /// <summary>Unsupported data, broken files and files too long to hold fail with the exceptions the apps show, not others.</summary>
    [Fact]
    public void FailCleanly()
    {
        // Unknown, too long
        Assert.Throws<NotSupportedException>(() => AudioFile.Decode(new byte[100]));
        Assert.Throws<NotSupportedException>(() => AudioFile.Decode(Read("tone_44k_stereo_l0.flac"), maxSamples: 1000));

        // Done: each format cut in half decodes what is there, or says it is broken
        foreach (string name in new[] { "tone_44k_stereo.wav", "mpeg1_44k_joint_vbr.mp3", "tone_44k_stereo_l12.flac", "tone_48k_stereo.opus", "tone_44k_stereo.ogg" })
        {
            byte[] data = Read(name);
            try { Assert.True(AudioFile.Decode(data[..(data.Length / 2)]).FrameCount > 0, name); }
            catch (InvalidDataException) { }
        }
    }

    /// <summary>Return the bytes of a test file.</summary>
    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Resources, name));

    /// <summary>Decode a test file.</summary>
    private static AudioClip Decode(string name) => AudioFile.Decode(Read(name));

    /// <summary>Return the samples of a reference WAV as floats, with its format.</summary>
    private static float[] Reference(string name, out int sampleRate, out int channelCount)
    {
        // All samples
        using WaveFileReader reader = new(Path.Combine(Resources, name));
        (sampleRate, channelCount) = (reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        ISampleProvider provider = reader.ToSampleProvider();
        List<float> samples = [];
        float[] buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0) samples.AddRange(buffer.AsSpan(0, read));
        // Return them
        return [.. samples];
    }
}
