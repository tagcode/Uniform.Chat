namespace Uniform.Matrix.Client.Android.Audio;

using global::Android.Content;
using global::Android.Media;
using NAudio.Wave;
using Uniform.Matrix.Audio;

/// <summary>The phone's earpiece or loudspeaker through <see cref="AudioTrack"/>, as an NAudio <see cref="IWavePlayer"/> playing the voice format as call audio.</summary>
/// <remarks>Plays in <see cref="CommunicationMode"/>, so call audio goes to the earpiece and the phone's echo cancellation hears it; for the loudspeaker, the built-in speaker becomes the communication device.</remarks>
/// <param name="context">Context for the audio service.</param>
/// <param name="loudspeaker">Whether to play through the loudspeaker rather than the earpiece.</param>
public sealed class AndroidSpeaker(Context context, bool loudspeaker) : IWavePlayer
{
    private const int ChunkBytes = VoiceFormat.FrameSamples * 2;
    private readonly AudioManager? audio = context.GetSystemService(Context.AudioService) as AudioManager;
    private IWaveProvider? source;
    private AudioTrack? track;
    private Thread? writer;
    private volatile bool playing;

    /// <summary>Playing, paused or stopped.</summary>
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    /// <summary>Volume of the track, 0 to 1.</summary>
    public float Volume { get; set; } = 1;

    /// <summary>The voice format.</summary>
    public WaveFormat OutputWaveFormat => VoiceFormat.WaveFormat;

    /// <summary>Raised when playing ends, with the error that ended it, if any.</summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>Play from <paramref name="waveProvider"/>, which must give the voice format.</summary>
    public void Init(IWaveProvider waveProvider)
    {
        // Assert the voice format
        if (waveProvider.WaveFormat.SampleRate != VoiceFormat.SampleRate || waveProvider.WaveFormat.Channels != 1 || waveProvider.WaveFormat.BitsPerSample != 16) throw new ArgumentException("The speaker plays 48 kHz mono 16-bit.", nameof(waveProvider));
        source = waveProvider;
    }

    /// <summary>Start playing on a thread of its own.</summary>
    public void Play()
    {
        // Assert a source, not already playing
        if (source is null) throw new InvalidOperationException("Init first.");
        if (playing) return;
        CommunicationMode.Enter(context);
        RouteToLoudspeaker(loudspeaker);
        AudioAttributes attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.VoiceCommunication)!.SetContentType(AudioContentType.Speech)!.Build()!;
        AudioFormat format = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(VoiceFormat.SampleRate)!.SetChannelMask(ChannelOut.Mono)!.Build()!;
        int minimum = AudioTrack.GetMinBufferSize(VoiceFormat.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        track = new AudioTrack.Builder().SetAudioAttributes(attributes)!.SetAudioFormat(format)!.SetTransferMode(AudioTrackMode.Stream)!.SetBufferSizeInBytes(Math.Max(minimum, ChunkBytes * 4))!.Build()!;
        track.SetVolume(Volume);

        // Write until stopped
        playing = true;
        PlaybackState = PlaybackState.Playing;
        track.Play();
        writer = new Thread(WriteLoop) { IsBackground = true, Name = "Speaker" };
        writer.Start();
    }

    /// <summary>Pause; playing again resumes.</summary>
    public void Pause() => Stop();

    /// <summary>Stop playing and wait for the thread.</summary>
    public void Stop()
    {
        // The thread ends at its next write
        if (!playing) return;
        playing = false;
        writer?.Join(1000);
        writer = null;
        PlaybackState = PlaybackState.Stopped;
    }

    /// <summary>Stop and free the track.</summary>
    public void Dispose()
    {
        // Stop, then release
        Stop();
        track?.Release();
        track = null;
    }

    /// <summary>Write 20 ms pieces from the source, until stopped or failed; a source with nothing gives silence.</summary>
    private void WriteLoop()
    {
        // Each write blocks until the track has room
        Exception? error = null;
        try
        {
            byte[] buffer = new byte[ChunkBytes];
            while (playing && track is AudioTrack current && source is IWaveProvider provider)
            {
                int read = provider.Read(buffer.AsSpan());
                if (read < buffer.Length) Array.Clear(buffer, read, buffer.Length - read);
                int written = current.Write(buffer, 0, buffer.Length);
                if (written < 0) throw new InvalidOperationException($"Writing to the speaker failed with code {written}.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex;
        }
        finally
        {
            track?.Stop();
            RouteToLoudspeaker(false);
            CommunicationMode.Exit(context);
            playing = false;
            PlaybackState = PlaybackState.Stopped;
        }
        // Done
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    /// <summary>Route call audio to the loudspeaker, or back to the default; the mode is <see cref="CommunicationMode"/>'s.</summary>
    private void RouteToLoudspeaker(bool on)
    {
        // Only when asked for, and back
        if (audio is null || !loudspeaker) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            if (!on)
            {
                audio.ClearCommunicationDevice();
                return;
            }
            AudioDeviceInfo? builtIn = audio.AvailableCommunicationDevices.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinSpeaker);
            if (builtIn is not null) audio.SetCommunicationDevice(builtIn);
            return;
        }
        // Older versions switch the speakerphone
#pragma warning disable CA1422
        audio.SpeakerphoneOn = on;
#pragma warning restore CA1422
    }
}
