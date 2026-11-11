namespace Uniform.Matrix.Audio;

using NAudio.Wave;

/// <summary>A ring tone for an incoming call, played on a speaker until disposed: two bursts of a two-tone chord, then a pause, repeated; for platforms without a ringtone of their own, such as Windows.</summary>
public sealed class RingTone : IDisposable
{
    /// <summary>The tone's frequencies in hertz, played together.</summary>
    private static readonly double[] Frequencies = [440, 480];
    /// <summary>Peak level of the chord, a little below full scale.</summary>
    private const double Level = 0.3;
    private readonly IWavePlayer? speaker;

    /// <summary>Play on <paramref name="speaker"/>; null plays nothing.</summary>
    private RingTone(IWavePlayer? speaker)
    {
        // Looping until disposed
        this.speaker = speaker;
        if (speaker is null) return;
        speaker.Init(new Tone());
        // Done
        speaker.Play();
    }

    /// <summary>Start ringing on the speaker of <paramref name="devices"/>; a speaker that does not open rings silently rather than failing the call.</summary>
    public static RingTone Start(IAudioDevices? devices)
    {
        // The speaker, if any
        IWavePlayer? speaker = null;
        try
        {
            speaker = devices?.OpenSpeaker();
            return new RingTone(speaker);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // On a speaker that did not open: no sound
            speaker?.Dispose();
            return new RingTone(null);
        }
    }

    /// <summary>Stop ringing.</summary>
    public void Dispose()
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
