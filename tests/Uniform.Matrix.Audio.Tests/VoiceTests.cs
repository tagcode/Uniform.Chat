namespace Uniform.Matrix.Audio.Tests;

using NAudio.Wave;

/// <summary>Tests of the voice path without real devices: Opus round trips, microphone format conversion and speaker buffering.</summary>
public class VoiceTests
{
    /// <summary>A tone survives Opus at its pitch and roughly its level, in packets the size speech at 32 kbit/s takes.</summary>
    [Fact]
    public void RoundTripThroughOpus()
    {
        // Arrange: a second of 440 Hz
        VoiceEncoder encoder = new();
        VoiceDecoder decoder = new();
        short[] input = Tone(440, VoiceFormat.SampleRate, seconds: 1, amplitude: 0.3);

        // Act
        List<short> output = [];
        int bytes = 0;
        for (int offset = 0; offset < input.Length; offset += VoiceFormat.FrameSamples)
        {
            byte[] packet = encoder.Encode(input.AsSpan(offset, VoiceFormat.FrameSamples));
            bytes += packet.Length;
            output.AddRange(decoder.Decode(packet));
        }

        // Assert: after the codec has settled
        short[] settled = [.. output.Skip(VoiceFormat.SampleRate / 5)];
        Assert.InRange(Frequency(settled, VoiceFormat.SampleRate), 430, 450);
        Assert.InRange(Rms(settled) / Rms(input), 0.6, 1.4);
        Assert.InRange(bytes / 50, 20, 200);
    }

    /// <summary>A packet that never arrived still gives a whole frame, concealed from the ones before.</summary>
    [Fact]
    public void ConcealLostPackets()
    {
        // Arrange: a few frames of tone
        VoiceEncoder encoder = new();
        VoiceDecoder decoder = new();
        short[] input = Tone(300, VoiceFormat.SampleRate, seconds: 0.2, amplitude: 0.3);
        for (int offset = 0; offset < input.Length; offset += VoiceFormat.FrameSamples) decoder.Decode(encoder.Encode(input.AsSpan(offset, VoiceFormat.FrameSamples)));

        // Act
        short[] concealed = decoder.Decode(null);

        // Assert
        Assert.Equal(VoiceFormat.FrameSamples, concealed.Length);
        Assert.True(Rms(concealed) > 100);
    }

    /// <summary>Frames must be exactly 20 ms.</summary>
    [Fact]
    public void RejectPartialFrames()
    {
        // Act and assert
        Assert.Throws<ArgumentException>(() => new VoiceEncoder().Encode(new short[100]));
    }

    /// <summary>A microphone recording 44.1 kHz stereo floats gives 48 kHz mono frames of 20 ms, as many as the recording is long, at the same pitch.</summary>
    [Fact]
    public void ConvertMicrophoneFormat()
    {
        // Arrange: a second of 1 kHz in both channels, delivered in 10 ms pieces
        FakeMicrophone device = new(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        using MicrophoneFrames microphone = new(device);
        List<short[]> frames = [];
        microphone.FrameCaptured += frames.Add;
        short[] tone = Tone(1000, 44100, seconds: 1, amplitude: 0.5);

        // Act
        microphone.Start();
        for (int offset = 0; offset < tone.Length; offset += 441) device.Deliver(tone.AsSpan(offset, 441));

        // Assert: about 50 frames, the resampler holding a little back
        Assert.True(device.Recording);
        Assert.InRange(frames.Count, 48, 50);
        Assert.All(frames, f => Assert.Equal(VoiceFormat.FrameSamples, f.Length));
        short[] joined = [.. frames.Skip(2).SelectMany(f => f)];
        Assert.InRange(Frequency(joined, VoiceFormat.SampleRate), 990, 1010);
        Assert.InRange(Rms(joined) / (0.5 * 32767 / Math.Sqrt(2)), 0.8, 1.2);
    }

    /// <summary>Frames queued for the speaker come out of the player as they went in, and stopping forgets the rest.</summary>
    [Fact]
    public void QueueSpeakerFrames()
    {
        // Arrange
        FakeSpeaker device = new();
        using SpeakerFrames speaker = new(device);
        short[] frame = Tone(500, VoiceFormat.SampleRate, seconds: 0.02, amplitude: 0.2);

        // Act
        speaker.Start();
        speaker.Play(frame);
        speaker.Play(frame);
        TimeSpan buffered = speaker.Buffered;
        byte[] played = new byte[frame.Length * 2];
        device.Source!.Read(played);
        speaker.Stop();

        // Assert
        Assert.Equal(PlaybackState.Stopped, device.PlaybackState);
        Assert.Equal(TimeSpan.FromMilliseconds(40), buffered);
        Assert.Equal(frame, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(played).ToArray());
        Assert.Equal(TimeSpan.Zero, speaker.Buffered);
    }

    /// <summary>Return a sine tone as 16-bit samples.</summary>
    private static short[] Tone(double frequency, int sampleRate, double seconds, double amplitude) =>
        [.. Enumerable.Range(0, (int)(sampleRate * seconds)).Select(i => (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * frequency * i / sampleRate)))];

    /// <summary>Return the frequency of a tone from its rising zero crossings.</summary>
    private static double Frequency(short[] samples, int sampleRate)
    {
        // Count upward crossings
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++) if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
        // Return crossings per second
        return crossings * (double)sampleRate / samples.Length;
    }

    /// <summary>Return the root mean square of samples.</summary>
    private static double Rms(short[] samples) => Math.Sqrt(samples.Average(s => (double)s * s));

    /// <summary>A microphone that delivers what the test gives it, as float samples in every channel.</summary>
    private sealed class FakeMicrophone(WaveFormat format) : IWaveIn
    {
        /// <summary>Format of the delivered data.</summary>
        public WaveFormat WaveFormat { get; set; } = format;
        /// <summary>Whether recording was started.</summary>
        public bool Recording { get; private set; }
        /// <summary>Raised with delivered data.</summary>
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }

        /// <summary>Start.</summary>
        public void StartRecording() => Recording = true;
        /// <summary>Stop.</summary>
        public void StopRecording() => Recording = false;
        /// <summary>Nothing to free.</summary>
        public void Dispose() { }

        /// <summary>Deliver samples, the same in every channel, as the device format's floats.</summary>
        public void Deliver(ReadOnlySpan<short> samples)
        {
            // Interleaved floats
            int channels = WaveFormat.Channels;
            float[] floats = new float[samples.Length * channels];
            for (int i = 0; i < samples.Length; i++) for (int channel = 0; channel < channels; channel++) floats[i * channels + channel] = samples[i] / 32767f;
            byte[] bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
    }

    /// <summary>A speaker that keeps the provider it plays from, for the test to read.</summary>
    private sealed class FakeSpeaker : IWavePlayer
    {
        /// <summary>What it would play.</summary>
        public IWaveProvider? Source { get; private set; }
        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Volume, unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>Format played.</summary>
        public WaveFormat OutputWaveFormat => Source!.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }

        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => Source = waveProvider;
        /// <summary>Play.</summary>
        public void Play() => PlaybackState = PlaybackState.Playing;
        /// <summary>Pause.</summary>
        public void Pause() => PlaybackState = PlaybackState.Paused;
        /// <summary>Stop.</summary>
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        /// <summary>Nothing to free.</summary>
        public void Dispose() { }
    }
}
