namespace Uniform.Matrix.Audio;

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Alsa;
using NAudio.Wave.SampleProviders;

/// <summary>A microphone or speaker a platform offers.</summary>
/// <param name="Name">Name to choose it by, e.g. an ALSA device name such as <c>pulse</c> or <c>plughw:CARD=MKII,DEV=0</c>.</param>
/// <param name="Description">What it is, for people.</param>
public sealed record AudioDevice(string Name, string Description);

/// <summary>Opens the microphone and speaker of a platform, as NAudio devices.</summary>
public interface IAudioDevices
{
    /// <summary>Open the microphone, not yet recording.</summary>
    IWaveIn OpenMicrophone();

    /// <summary>Open the speaker, not yet initialized.</summary>
    IWavePlayer OpenSpeaker();

    /// <summary>Return the microphones there are.</summary>
    IReadOnlyList<AudioDevice> ListMicrophones();

    /// <summary>Return the speakers there are.</summary>
    IReadOnlyList<AudioDevice> ListSpeakers();
}

/// <summary>The audio devices of the platform the app runs on.</summary>
public static class AudioDevices
{
    /// <summary>Return the devices of this platform: ALSA on Linux, WASAPI on Windows; null elsewhere, e.g. on Android, whose app supplies its own <see cref="IAudioDevices"/>.</summary>
    /// <param name="microphone">Microphone to use, by name; the system's default when null or empty.</param>
    /// <param name="speaker">Speaker to use, by name; the system's default when null or empty.</param>
    public static IAudioDevices? ForThisPlatform(string? microphone = null, string? speaker = null)
    {
        // By operating system
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return new AlsaAudioDevices(microphone, speaker);
        if (OperatingSystem.IsWindows()) return new WasapiAudioDevices(microphone, speaker);
        // Return none
        return null;
    }
}

/// <summary>ALSA devices by name; the default ones route through PipeWire or PulseAudio on desktops, so the system's sound settings apply.</summary>
/// <remarks>The <c>default</c> and <c>pulse</c> devices follow the <c>PULSE_SOURCE</c> and <c>PULSE_SINK</c> environment variables, which choose a PipeWire or PulseAudio device for one process, e.g. a virtual one in tests.</remarks>
/// <param name="microphone">ALSA capture device name; <c>default</c> when null or empty.</param>
/// <param name="speaker">ALSA playback device name; <c>default</c> when null or empty.</param>
[SupportedOSPlatform("linux")]
public sealed class AlsaAudioDevices(string? microphone = null, string? speaker = null) : IAudioDevices
{
    private const string Default = "default";
    private readonly string microphone = string.IsNullOrWhiteSpace(microphone) ? Default : microphone;
    private readonly string speaker = string.IsNullOrWhiteSpace(speaker) ? Default : speaker;

    /// <summary>Open the capture device, asking for the voice format, which ALSA's plug devices convert to.</summary>
    public IWaveIn OpenMicrophone() => new AlsaIn(microphone) { WaveFormat = VoiceFormat.WaveFormat };

    /// <summary>Open the playback device.</summary>
    public IWavePlayer OpenSpeaker() => new AlsaOut(speaker);

    /// <summary>Return ALSA's capture devices.</summary>
    public IReadOnlyList<AudioDevice> ListMicrophones() => [.. AlsaDeviceEnumerator.GetCaptureDevices().Select(d => new AudioDevice(d.Name, d.Description))];

    /// <summary>Return ALSA's playback devices.</summary>
    public IReadOnlyList<AudioDevice> ListSpeakers() => [.. AlsaDeviceEnumerator.GetPlaybackDevices().Select(d => new AudioDevice(d.Name, d.Description))];
}

/// <summary>Windows devices through WASAPI, in shared mode, chosen by part of their friendly name, e.g. <c>Headset</c>.</summary>
/// <remarks>Uses NAudio's <c>WasapiCapture</c> and <c>WasapiOut</c>, which NAudio 3 marks obsolete in favour of builders whose recorder no longer is an <see cref="IWaveIn"/>; these still are, and work. Not yet tried on Windows.</remarks>
/// <param name="microphone">Part of the microphone's name; the default one when null or empty, or when none matches.</param>
/// <param name="speaker">Part of the speaker's name; the default one when null or empty, or when none matches.</param>
[SupportedOSPlatform("windows")]
#pragma warning disable CS0618
public sealed class WasapiAudioDevices(string? microphone = null, string? speaker = null) : IAudioDevices
{
    private const int LatencyMilliseconds = 60;

    /// <summary>Open the capture device; it records in the system's mix format, which <see cref="MicrophoneFrames"/> converts.</summary>
    public IWaveIn OpenMicrophone() => Find(DataFlow.Capture, microphone) is MMDevice device ? new WasapiCapture(device) : new WasapiCapture();

    /// <summary>Open the playback device, fed in its own mix format, as WASAPI's shared mode does not convert sample rates.</summary>
    public IWavePlayer OpenSpeaker()
    {
        // The chosen device, else the default one
        using MMDeviceEnumerator enumerator = new();
        MMDevice device = Find(DataFlow.Render, speaker) ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        WaveFormat mix = device.AudioClient.MixFormat;
        // Return the converting speaker
        return new MixFormatSpeaker(new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMilliseconds), mix);
    }

    /// <summary>Return the active capture devices.</summary>
    public IReadOnlyList<AudioDevice> ListMicrophones() => List(DataFlow.Capture);

    /// <summary>Return the active playback devices.</summary>
    public IReadOnlyList<AudioDevice> ListSpeakers() => List(DataFlow.Render);

    /// <summary>Return the active devices of a direction by friendly name.</summary>
    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        // Active endpoints
        using MMDeviceEnumerator enumerator = new();
        // Return names
        return [.. enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => new AudioDevice(d.FriendlyName, d.FriendlyName))];
    }

    /// <summary>Return the first active device whose friendly name contains <paramref name="name"/>, or null for the default.</summary>
    private static MMDevice? Find(DataFlow flow, string? name)
    {
        // Default when not named
        if (string.IsNullOrWhiteSpace(name)) return null;
        using MMDeviceEnumerator enumerator = new();
        // Return the match, if any
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
#pragma warning restore CS0618

/// <summary>A speaker that converts what it plays to <paramref name="mix"/>, the device's mix format: its sample rate and channels, in 32-bit float, so the device takes the audio as it is.</summary>
/// <param name="speaker">The device's player.</param>
/// <param name="mix">The device's mix format.</param>
internal sealed class MixFormatSpeaker(IWavePlayer speaker, WaveFormat mix) : IWavePlayer
{
    /// <summary>The device's volume.</summary>
    public float Volume { get => speaker.Volume; set => speaker.Volume = value; }

    /// <summary>Whether it plays.</summary>
    public PlaybackState PlaybackState => speaker.PlaybackState;

    /// <summary>The format the device gets.</summary>
    public WaveFormat OutputWaveFormat => speaker.OutputWaveFormat;

    /// <summary>Raised when playing stops, with the exception when it failed.</summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped
    {
        add => speaker.PlaybackStopped += value;
        remove => speaker.PlaybackStopped -= value;
    }

    /// <summary>Play <paramref name="waveProvider"/> resampled, and spread over the device's channels.</summary>
    public void Init(IWaveProvider waveProvider)
    {
        // Rate, then channels
        ISampleProvider samples = waveProvider.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != mix.SampleRate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        if (samples.WaveFormat.Channels == 1 && mix.Channels > 1) samples = new SpreadSampleProvider(samples, mix.Channels);
        // Done
        speaker.Init(new SampleToWaveProvider(samples));
    }

    /// <summary>Start playing.</summary>
    public void Play() => speaker.Play();

    /// <summary>Stop playing.</summary>
    public void Stop() => speaker.Stop();

    /// <summary>Pause playing.</summary>
    public void Pause() => speaker.Pause();

    /// <summary>Close the device.</summary>
    public void Dispose() => speaker.Dispose();

    /// <summary>Mono samples copied to every channel.</summary>
    private sealed class SpreadSampleProvider(ISampleProvider mono, int channels) : ISampleProvider
    {
        private float[] buffer = [];

        /// <summary>The mono source's rate over the channels.</summary>
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, channels);

        /// <summary>Read whole frames of all channels.</summary>
        public int Read(Span<float> destination)
        {
            // Mono samples for the frames that fit
            int frames = destination.Length / channels;
            if (buffer.Length < frames) buffer = new float[frames];
            int read = mono.Read(buffer.AsSpan(0, frames));
            for (int i = 0; i < read; i++) destination.Slice(i * channels, channels).Fill(buffer[i]);
            // Return samples written
            return read * channels;
        }
    }
}
