namespace Uniform.Matrix.Audio;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>Turns what a microphone records, in whatever format its device gives, into 20 ms voice frames: mixed to mono and resampled to 48 kHz.</summary>
/// <remarks>Frames are raised on the microphone's own thread, as it delivers data.</remarks>
public sealed class MicrophoneFrames : IDisposable
{
    private readonly IWaveIn microphone;
    private readonly BufferedWaveProvider recorded;
    private readonly ISampleProvider voice;
    private readonly float[] chunk = new float[VoiceFormat.FrameSamples];
    private readonly List<float> pending = [];

    /// <summary>Follow <paramref name="microphone"/>, whose <see cref="IWaveIn.WaveFormat"/> is read now, so set it before.</summary>
    public MicrophoneFrames(IWaveIn microphone)
    {
        // Device format in, mono 48 kHz out
        this.microphone = microphone;
        recorded = new BufferedWaveProvider(microphone.WaveFormat, TimeSpan.FromSeconds(2)) { ReadFully = false, DiscardOnBufferOverflow = true };
        ISampleProvider samples = recorded.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1) samples = new DownmixSampleProvider(samples);
        voice = samples.WaveFormat.SampleRate == VoiceFormat.SampleRate ? samples : new WdlResamplingSampleProvider(samples, VoiceFormat.SampleRate);
        microphone.DataAvailable += OnDataAvailable;
    }

    /// <summary>Raised with each frame of <see cref="VoiceFormat.FrameSamples"/> samples.</summary>
    public event Action<short[]>? FrameCaptured;

    /// <summary>Start recording.</summary>
    public void Start() => microphone.StartRecording();

    /// <summary>Stop recording.</summary>
    public void Stop() => microphone.StopRecording();

    /// <summary>Stop following the microphone, and close it.</summary>
    public void Dispose()
    {
        // Unhook, then close the device
        microphone.DataAvailable -= OnDataAvailable;
        microphone.Dispose();
    }

    /// <summary>Convert recorded data and raise the frames it completes.</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // All that converts now, then whole frames
        recorded.AddSamples(e.Buffer, 0, e.BytesRecorded);
        for (int read = voice.Read(chunk); read > 0; read = voice.Read(chunk)) pending.AddRange(chunk.AsSpan(0, read));
        while (pending.Count >= VoiceFormat.FrameSamples)
        {
            short[] frame = new short[VoiceFormat.FrameSamples];
            for (int i = 0; i < frame.Length; i++) frame[i] = (short)Math.Clamp(pending[i] * 32767f, short.MinValue, short.MaxValue);
            pending.RemoveRange(0, VoiceFormat.FrameSamples);
            FrameCaptured?.Invoke(frame);
        }
    }

    /// <summary>Mixes any number of channels to mono by averaging them.</summary>
    private sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int channels = source.WaveFormat.Channels;
        private float[] buffer = [];

        /// <summary>Mono at the source's rate.</summary>
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        /// <summary>Fill <paramref name="samples"/> with mono samples, or part of it; return how many.</summary>
        public int Read(Span<float> samples)
        {
            // Source frames for the samples asked
            if (buffer.Length < samples.Length * channels) buffer = new float[samples.Length * channels];
            int read = source.Read(buffer.AsSpan(0, samples.Length * channels)) / channels;
            for (int i = 0; i < read; i++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++) sum += buffer[i * channels + channel];
                samples[i] = sum / channels;
            }
            // Return mono samples
            return read;
        }
    }
}
