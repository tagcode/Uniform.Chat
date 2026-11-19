namespace Uniform.Matrix.Audio;

using NAudio.Wave;

/// <summary>A tone of a call played on a speaker until disposed: the ring of an incoming call, for platforms without a ringtone of their own, such as desktops; or the ringback a caller hears while the call is answered and connects.</summary>
/// <remarks>The speaker opens and closes on a thread-pool thread: WASAPI's objects made on a UI thread, a single-threaded COM apartment on Windows, fail on NAudio's playback thread.</remarks>
public sealed class RingTone : IDisposable
{
    /// <summary>The ring's frequencies in hertz, played together.</summary>
    private static readonly double[] Frequencies = [440, 480];
    /// <summary>Peak level of the ring's chord, a little below full scale.</summary>
    private const double Level = 0.3;
    /// <summary>The ringback's frequency in hertz, the European one.</summary>
    private const double RingbackFrequency = 425;
    /// <summary>Peak level of the ringback, quieter than the ring, as it plays close to the ear.</summary>
    private const double RingbackLevel = 0.15;
    private readonly Task<IWavePlayer?> starting;

    /// <summary>Ring once <paramref name="starting"/> has the speaker playing.</summary>
    private RingTone(Task<IWavePlayer?> starting) => this.starting = starting;

    /// <summary>Raised when the speaker fails to open or play, so the app can log why there is no ring.</summary>
    public static event Action<Exception>? Failed;

    /// <summary>Start ringing on the speaker of <paramref name="devices"/>, without waiting for it to open; a speaker that does not open rings silently rather than failing the call.</summary>
    public static RingTone Start(IAudioDevices? devices) => new(Task.Run(() => Open(devices, Sample)));

    /// <summary>Start the ringback of an outgoing call on the speaker of <paramref name="devices"/>, without waiting for it to open; a speaker that does not open stays silent rather than failing the call.</summary>
    public static RingTone StartRingback(IAudioDevices? devices) => new(Task.Run(() => Open(devices, RingbackSample)));

    /// <summary>Stop ringing, once the speaker has opened if it is still opening.</summary>
    public void Dispose() => starting.ContinueWith(task => Close(task.Result), TaskScheduler.Default);

    /// <summary>Open the speaker and play the tone of <paramref name="sample"/>; null on no speaker or one that fails.</summary>
    private static IWavePlayer? Open(IAudioDevices? devices, Func<long, double> sample)
    {
        // The speaker, if any
        IWavePlayer? speaker = null;
        try
        {
            speaker = devices?.OpenSpeaker();
            if (speaker is null) return null;
            speaker.PlaybackStopped += (_, stopped) => { if (stopped.Exception is Exception e) Failed?.Invoke(e); };
            speaker.Init(new Tone(sample));
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

    /// <summary>Return the sample of the ringback at <paramref name="index"/> since the start, between -1 and 1: 1 s on, 4 s off.</summary>
    public static double RingbackSample(long index)
    {
        // The place in the 5 s cycle
        if (index % (VoiceFormat.SampleRate * 5) >= VoiceFormat.SampleRate) return 0;
        double time = (double)index / VoiceFormat.SampleRate;
        // Return the tone
        return RingbackLevel * Math.Sin(2 * Math.PI * RingbackFrequency * time);
    }

    /// <summary>A tone as 16-bit PCM in <see cref="VoiceFormat"/>, without end.</summary>
    /// <param name="sample">Returns the sample at an index since the start, between -1 and 1.</param>
    private sealed class Tone(Func<long, double> sample) : IWaveProvider
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
                short value = (short)(sample(index++) * short.MaxValue);
                buffer[i * 2] = (byte)value;
                buffer[i * 2 + 1] = (byte)(value >> 8);
            }
            // Return bytes written
            return samples * 2;
        }
    }
}
