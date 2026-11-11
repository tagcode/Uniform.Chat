namespace Uniform.Matrix.Audio;

using NAudio.Wave;

/// <summary>A ring tone for an incoming call, played on a speaker until disposed: two bursts of a two-tone chord, then a pause, repeated; for platforms without a ringtone of their own, such as desktops.</summary>
/// <remarks>The speaker opens and closes on a thread-pool thread: WASAPI's objects made on a UI thread, a single-threaded COM apartment on Windows, fail on NAudio's playback thread.</remarks>
public sealed class RingTone : IDisposable
{
    /// <summary>The tone's frequencies in hertz, played together.</summary>
    private static readonly double[] Frequencies = [440, 480];
    /// <summary>Peak level of the chord, a little below full scale.</summary>
    private const double Level = 0.3;
    private readonly Task<IWavePlayer?> starting;

    /// <summary>Ring once <paramref name="starting"/> has the speaker playing.</summary>
    private RingTone(Task<IWavePlayer?> starting) => this.starting = starting;

    /// <summary>Raised when the speaker fails to open or play, so the app can log why there is no ring.</summary>
    public static event Action<Exception>? Failed;

    /// <summary>Start ringing on the speaker of <paramref name="devices"/>, without waiting for it to open; a speaker that does not open rings silently rather than failing the call.</summary>
    public static RingTone Start(IAudioDevices? devices) => new(Task.Run(() => Open(devices)));

    /// <summary>Stop ringing, once the speaker has opened if it is still opening.</summary>
    public void Dispose() => starting.ContinueWith(task => Close(task.Result), TaskScheduler.Default);

    /// <summary>Open the speaker and play the tone; null on no speaker or one that fails.</summary>
    private static IWavePlayer? Open(IAudioDevices? devices)
    {
        // The speaker, if any
        IWavePlayer? speaker = null;
        try
        {
            speaker = devices?.OpenSpeaker();
            if (speaker is null) return null;
            speaker.PlaybackStopped += (_, stopped) => { if (stopped.Exception is Exception e) Failed?.Invoke(e); };
            speaker.Init(new Tone());
            speaker.Play();
            // Return the playing speaker
            return speaker;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // On a speaker that did not open: no sound
            Failed?.Invoke(e);
            speaker?.Dispose();
            return null;
        }
    }

    /// <summary>Stop and close the speaker.</summary>
    private static void Close(IWavePlayer? speaker)
    {
        // Stopped, then closed
        if (speaker is null) return;
        try { speaker.Stop(); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        // Done
        speaker.Dispose();
    }

    /// <summary>Return the sample of the ring at <paramref name="index"/> since the start, between -1 and 1: 0.4 s on, 0.2 s off, 0.4 s on, 2 s off.</summary>
    public static double Sample(long index)
    {
        // The place in the 3 s cycle
        double second = (double)(index % (VoiceFormat.SampleRate * 3)) / VoiceFormat.SampleRate;
        bool on = second < 0.4 || second is >= 0.6 and < 1.0;
        if (!on) return 0;
        double time = (double)index / VoiceFormat.SampleRate;
        // Return the chord
        return Level * Frequencies.Sum(f => Math.Sin(2 * Math.PI * f * time)) / Frequencies.Length;
    }

    /// <summary>The ring as 16-bit PCM in <see cref="VoiceFormat"/>, without end.</summary>
    private sealed class Tone : IWaveProvider
    {
        private long index;

        /// <summary>The voice format.</summary>
        public WaveFormat WaveFormat => VoiceFormat.WaveFormat;

        /// <summary>Fill <paramref name="buffer"/> with the next samples.</summary>
        public int Read(Span<byte> buffer)
        {
            // Two bytes per sample, little-endian
            int samples = buffer.Length / 2;
            for (int i = 0; i < samples; i++)
            {
                short value = (short)(Sample(index++) * short.MaxValue);
                buffer[i * 2] = (byte)value;
                buffer[i * 2 + 1] = (byte)(value >> 8);
            }
            // Return bytes written
            return samples * 2;
        }
    }
}
