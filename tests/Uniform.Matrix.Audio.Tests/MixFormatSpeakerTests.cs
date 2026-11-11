namespace Uniform.Matrix.Audio.Tests;

using NAudio.Wave;

/// <summary>Tests of <see cref="MixFormatSpeaker"/>.</summary>
public sealed class MixFormatSpeakerTests
{
    /// <summary>Play voice to a device that mixes at another rate over two channels: the device gets its own format, the tone the same on each channel, at its pitch.</summary>
    [Fact]
    public void ConvertToMixFormat()
    {
        // A 44.1 kHz stereo device, fed 48 kHz mono voice of a 1 kHz tone
        FakePlayer device = new();
        using MixFormatSpeaker speaker = new(device, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        BufferedWaveProvider voice = new(VoiceFormat.WaveFormat, TimeSpan.FromSeconds(2));
        short[] tone = [.. Enumerable.Range(0, VoiceFormat.SampleRate).Select(i => (short)(10000 * Math.Sin(2 * Math.PI * 1000 * i / VoiceFormat.SampleRate)))];
        voice.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tone.AsSpan()).ToArray(), 0, tone.Length * 2);
        speaker.Init(voice);

        // Half a second as the device reads it
        Assert.Equal(44100, device.Source!.WaveFormat.SampleRate);
        Assert.Equal(2, device.Source.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, device.Source.WaveFormat.Encoding);
        byte[] bytes = new byte[44100 * 2 * 4 / 2];
        int read = device.Source.Read(bytes);
        float[] samples = new float[read / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, read);

        // Both channels alike; the tone's zero crossings at 1 kHz
        Assert.Equal(bytes.Length, read);
        for (int i = 0; i < samples.Length; i += 2) Assert.Equal(samples[i], samples[i + 1]);
        float[] left = [.. samples.Where((_, i) => i % 2 == 0).Skip(4410)];
        int crossings = left.Zip(left.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        Assert.InRange(crossings, 390, 410);
        // Done
        Assert.InRange(left.Max(), 0.25f, 0.35f);
    }

    /// <summary>A player that only keeps what it is given.</summary>
    private sealed class FakePlayer : IWavePlayer
    {
        /// <summary>What it was given to play.</summary>
        public IWaveProvider? Source { get; private set; }

        /// <summary>Unused.</summary>
        public float Volume { get; set; } = 1;

        /// <summary>Never playing.</summary>
        public PlaybackState PlaybackState => PlaybackState.Stopped;

        /// <summary>What it was given.</summary>
        public WaveFormat OutputWaveFormat => Source!.WaveFormat;

        /// <summary>Never raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }

        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => Source = waveProvider;

        /// <summary>Nothing.</summary>
        public void Play() { }

        /// <summary>Nothing.</summary>
        public void Stop() { }

        /// <summary>Nothing.</summary>
        public void Pause() { }

        /// <summary>Nothing.</summary>
        public void Dispose() { }
    }
}
