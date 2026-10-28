namespace Uniform.Matrix.Audio;

using System.Runtime.InteropServices;
using NAudio.Wave;

/// <summary>Plays 20 ms voice frames through a speaker, keeping a short buffer against uneven arrival; silence fills the gaps.</summary>
public sealed class SpeakerFrames : IDisposable
{
    private readonly IWavePlayer speaker;
    private readonly BufferedWaveProvider queued;

    /// <summary>Play through <paramref name="speaker"/>.</summary>
    /// <param name="speaker">Output device, not yet initialized.</param>
    /// <param name="maxBuffered">Most audio queued; frames beyond it are dropped, so delay cannot grow without bound.</param>
    public SpeakerFrames(IWavePlayer speaker, TimeSpan? maxBuffered = null)
    {
        // Voice format in; the device converts to its own
        this.speaker = speaker;
        queued = new BufferedWaveProvider(VoiceFormat.WaveFormat, maxBuffered ?? TimeSpan.FromMilliseconds(500)) { DiscardOnBufferOverflow = true };
        speaker.Init(queued);
    }

    /// <summary>Audio queued and not yet played.</summary>
    public TimeSpan Buffered => queued.BufferedDuration;

    /// <summary>Start playing.</summary>
    public void Start() => speaker.Play();

    /// <summary>Stop playing and forget what is queued.</summary>
    public void Stop()
    {
        // Silence at once
        speaker.Stop();
        queued.ClearBuffer();
    }

    /// <summary>Queue a frame of samples.</summary>
    public void Play(ReadOnlySpan<short> frame)
    {
        // As little-endian bytes
        queued.AddSamples(MemoryMarshal.AsBytes(frame));
    }

    /// <summary>Close the speaker.</summary>
    public void Dispose() => speaker.Dispose();
}
