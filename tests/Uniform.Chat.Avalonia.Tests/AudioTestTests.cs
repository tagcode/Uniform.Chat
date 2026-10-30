namespace Uniform.Matrix.Client.Avalonia.Core.Tests;

using NAudio.Wave;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;

/// <summary>The settings' audio test: asks for the microphone, loops it back through Opus, and reports what went through.</summary>
public class AudioTestTests
{
    /// <summary>Without the microphone permission nothing opens; with it, a recorded second is reported with its bit rate, and the loudspeaker choice reaches the devices.</summary>
    [Fact]
    public async Task LoopMicrophoneBack()
    {
        // Arrange: devices that record a second of tone when started
        bool granted = false;
        List<bool> opened = [];
        FakeMicrophone microphone = new();
        AudioTestViewModel test = new(loudspeaker => { opened.Add(loudspeaker); return new FakeDevices(microphone); }, hasEarpiece: true, () => Task.FromResult(granted));

        // Act: refused, then allowed on the earpiece
        await test.ToggleCommand.ExecuteAsync(null);
        string refused = test.Status;
        granted = true;
        test.Loudspeaker = false;
        await test.ToggleCommand.ExecuteAsync(null);
        bool running = test.IsRunning;
        microphone.DeliverSecond();
        await test.ToggleCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("permission", refused);
        Assert.Equal([false], opened);
        Assert.True(running);
        Assert.False(test.IsRunning);
        Assert.Matches(@"^Recorded 1 s, sent at \d+ kbit/s\.$", test.Status);
        Assert.False(microphone.Recording);
    }

    /// <summary>Devices around a fake microphone and a speaker that plays nothing.</summary>
    private sealed class FakeDevices(FakeMicrophone microphone) : IAudioDevices
    {
        /// <summary>The fake microphone.</summary>
        public IWaveIn OpenMicrophone() => microphone;
        /// <summary>A silent speaker.</summary>
        public IWavePlayer OpenSpeaker() => new SilentSpeaker();
        /// <summary>None listed.</summary>
        public IReadOnlyList<AudioDevice> ListMicrophones() => [];
        /// <summary>None listed.</summary>
        public IReadOnlyList<AudioDevice> ListSpeakers() => [];
    }

    /// <summary>A microphone that delivers a second of 300 Hz in the voice format when told.</summary>
    private sealed class FakeMicrophone : IWaveIn
    {
        /// <summary>The voice format.</summary>
        public WaveFormat WaveFormat { get; set; } = VoiceFormat.WaveFormat;
        /// <summary>Whether recording.</summary>
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
        public void Dispose() => Recording = false;

        /// <summary>Deliver a second of tone in 20 ms pieces.</summary>
        public void DeliverSecond()
        {
            // 50 pieces
            for (int piece = 0; piece < 50; piece++)
            {
                short[] samples = [.. Enumerable.Range(piece * 960, 960).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / 48000)))];
                byte[] bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
            }
        }
    }

    /// <summary>A speaker that takes what it is given and plays nothing.</summary>
    private sealed class SilentSpeaker : IWavePlayer
    {
        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>The voice format.</summary>
        public WaveFormat OutputWaveFormat => VoiceFormat.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        /// <summary>Ignore the source.</summary>
        public void Init(IWaveProvider waveProvider) { }
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
