namespace Uniform.Matrix.Calls.Tests;

using System.Runtime.InteropServices;
using NAudio.Wave;
using Uniform.Matrix.Audio;

/// <summary>Audio devices without hardware: a microphone that records a tone in real time, and a speaker that plays in real time into a measure of its energy.</summary>
/// <param name="frequency">Frequency of the microphone's tone.</param>
internal sealed class FakeAudio(double frequency) : IAudioDevices
{
    /// <summary>The speaker opened last.</summary>
    public FakeSpeaker? Speaker { get; private set; }

    /// <summary>Open a microphone of the tone.</summary>
    public IWaveIn OpenMicrophone() => new FakeMicrophone(frequency);

    /// <summary>Open a speaker.</summary>
    public IWavePlayer OpenSpeaker() => Speaker = new FakeSpeaker();

    /// <summary>None to list.</summary>
    public IReadOnlyList<AudioDevice> ListMicrophones() => [];

    /// <summary>None to list.</summary>
    public IReadOnlyList<AudioDevice> ListSpeakers() => [];

    /// <summary>Records a tone, 20 ms at a time.</summary>
    private sealed class FakeMicrophone(double frequency) : IWaveIn
    {
        private Timer? timer;
        private long sample;

        /// <summary>The voice format.</summary>
        public WaveFormat WaveFormat { get; set; } = VoiceFormat.WaveFormat;
        /// <summary>Raised every 20 ms.</summary>
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }

        /// <summary>Start the tone.</summary>
        public void StartRecording() => timer = new Timer(_ => Record(), null, 0, VoiceFormat.FrameMilliseconds);
        /// <summary>Stop the tone.</summary>
        public void StopRecording() => timer?.Dispose();
        /// <summary>Stop the tone.</summary>
        public void Dispose() => timer?.Dispose();

        /// <summary>Deliver a frame of the tone; one at a time, as a device's thread would.</summary>
        private void Record()
        {
            // One frame, continuing the phase
            if (!Monitor.TryEnter(this)) return;
            try { RecordFrame(); }
            finally { Monitor.Exit(this); }
        }

        /// <summary>Deliver the next frame.</summary>
        private void RecordFrame()
        {
            // Samples as bytes
            short[] frame = new short[VoiceFormat.FrameSamples];
            for (int i = 0; i < frame.Length; i++) frame[i] = (short)(8000 * Math.Sin(2 * Math.PI * frequency * sample++ / VoiceFormat.SampleRate));
            byte[] bytes = MemoryMarshal.AsBytes(frame.AsSpan()).ToArray();
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
    }

    /// <summary>Plays 20 ms at a time from its source, summing the energy it plays.</summary>
    internal sealed class FakeSpeaker : IWavePlayer
    {
        private IWaveProvider? source;
        private Timer? timer;
        private double energy;

        /// <summary>Sum of squared samples played.</summary>
        public double Energy => Volatile.Read(ref energy);
        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Volume, unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>Format played.</summary>
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }

        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => source = waveProvider;
        /// <summary>Play in real time.</summary>
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            timer = new Timer(_ => Pull(), null, 0, VoiceFormat.FrameMilliseconds);
        }
        /// <summary>Pause.</summary>
        public void Pause() => PlaybackState = PlaybackState.Paused;
        /// <summary>Stop.</summary>
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            timer?.Dispose();
        }
        /// <summary>Stop.</summary>
        public void Dispose() => timer?.Dispose();

        /// <summary>Play a frame from the source.</summary>
        private void Pull()
        {
            // 20 ms of 16-bit samples
            if (source is null) return;
            byte[] buffer = new byte[VoiceFormat.FrameSamples * 2];
            int read = source!.Read(buffer);
            double sum = 0;
            foreach (short value in MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, read & ~1))) sum += (double)value * value;
            // Done
            lock (this) energy += sum;
        }
    }
}
