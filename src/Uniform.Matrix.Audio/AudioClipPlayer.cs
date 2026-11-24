namespace Uniform.Matrix.Audio;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Uniform.Matrix.Audio.Decoding;

/// <summary>Plays an <see cref="AudioClip"/> on a speaker, one at a time: play, pause, resume, seek and stop, with the position played.</summary>
/// <remarks>
/// Clips are resampled to 48 kHz stereo, <see cref="OutputFormat"/>. Each start opens the speaker anew and a pause closes it, on the thread pool, as WASAPI's objects made on a UI thread fail on NAudio's playback thread.
/// At the clip's end, silence plays for <see cref="EndTail"/>, so the speaker's buffer is heard out, then the speaker closes and <see cref="Ended"/> is raised.
/// </remarks>
/// <param name="openSpeaker">Opens the speaker to play on, e.g. <see cref="IAudioDevices.OpenMediaSpeaker"/>.</param>
public sealed class AudioClipPlayer(Func<IWavePlayer> openSpeaker) : IAsyncDisposable
{
    /// <summary>The format the speaker gets: 48 kHz, stereo, 16-bit.</summary>
    public static WaveFormat OutputFormat { get; } = new(48000, 16, 2);

    /// <summary>How long silence plays after the clip's end, before the speaker closes.</summary>
    private static readonly TimeSpan EndTail = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim gate = new(1, 1);
    private ClipSource? source;
    private IWavePlayer? speaker;

    /// <summary>The clip playing or paused; null when stopped.</summary>
    public AudioClip? Clip => source?.Clip;

    /// <summary>Whether the clip plays now, rather than paused or stopped.</summary>
    public bool IsPlaying => speaker is not null;

    /// <summary>The position in the clip.</summary>
    public TimeSpan Position => source is ClipSource current ? TimeSpan.FromSeconds(current.Frame / (double)current.Clip.SampleRate) : TimeSpan.Zero;

    /// <summary>Raised when the clip has played to its end and the speaker closed, on the thread pool.</summary>
    public event Action<AudioClip>? Ended;

    /// <summary>Raised when the speaker fails to open or play, on any thread; playing stops.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Play <paramref name="clip"/> from <paramref name="from"/>, instead of any clip playing.</summary>
    public async Task PlayAsync(AudioClip clip, TimeSpan from = default)
    {
        // The new clip, on a new speaker
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            source = new ClipSource(clip) { Frame = FrameAt(clip, from) };
            await OpenAsync(source);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Pause, keeping the position; nothing when not playing.</summary>
    public async Task PauseAsync()
    {
        // The speaker closes
        await gate.WaitAsync();
        try { await CloseAsync(); }
        finally { gate.Release(); }
    }

    /// <summary>Resume a paused clip from its position; from the start when it had ended.</summary>
    public async Task ResumeAsync()
    {
        // A paused clip only
        await gate.WaitAsync();
        try
        {
            if (source is not ClipSource paused || speaker is not null) return;
            if (paused.Frame >= paused.Clip.FrameCount) paused.Frame = 0;
            await OpenAsync(paused);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Move to <paramref name="position"/> in the clip, playing or paused.</summary>
    public void Seek(TimeSpan position)
    {
        // The next samples read
        if (source is ClipSource current) current.Frame = FrameAt(current.Clip, position);
    }

    /// <summary>Stop, and forget the clip.</summary>
    public async Task StopAsync()
    {
        // Closed and cleared
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            source = null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stop and close the speaker.</summary>
    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Open a speaker playing <paramref name="playing"/>, converted to <see cref="OutputFormat"/>; on failure, raise <see cref="Failed"/>.</summary>
    private async Task OpenAsync(ClipSource playing)
    {
        // Channels, rate and sample size
        ISampleProvider samples = playing;
        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != OutputFormat.SampleRate) samples = new WdlResamplingSampleProvider(samples, OutputFormat.SampleRate);
        IWaveProvider output = new SampleToWaveProvider16(samples);

        // Opened and started on the thread pool
        IWavePlayer? opened = null;
        try
        {
            opened = await Task.Run(() =>
            {
                IWavePlayer player = openSpeaker();
                player.Init(output);
                player.Play();
                return player;
            });
            opened.PlaybackStopped += (_, stopped) => { if (stopped.Exception is Exception e) Fail(opened, e); };
            playing.EndReached = () => _ = EndAsync(playing);
            speaker = opened;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // On a speaker that did not open
            opened?.Dispose();
            Failed?.Invoke(exception);
        }
    }

    /// <summary>Close the speaker, if open, on the thread pool.</summary>
    private async Task CloseAsync()
    {
        // Stopped, then closed
        if (speaker is not IWavePlayer open) return;
        speaker = null;
        if (source is ClipSource current) current.EndReached = null;
        await Task.Run(() =>
        {
            try { open.Stop(); }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            open.Dispose();
        });
    }

    /// <summary>After the clip's end and the tail of silence, close the speaker and tell, unless something else played meanwhile.</summary>
    private async Task EndAsync(ClipSource ended)
    {
        // The tail, then closed
        await Task.Delay(EndTail);
        await gate.WaitAsync();
        try
        {
            if (source != ended || speaker is null || ended.Frame < ended.Clip.FrameCount) return;
            await CloseAsync();
        }
        finally
        {
            gate.Release();
        }
        // Done
        Ended?.Invoke(ended.Clip);
    }

    /// <summary>Close a speaker that failed while playing, and tell.</summary>
    private void Fail(IWavePlayer failed, Exception exception)
    {
        // Only the current one
        if (speaker != failed) return;
        speaker = null;
        _ = Task.Run(failed.Dispose);
        // Done
        Failed?.Invoke(exception);
    }

    /// <summary>Return the frame of <paramref name="position"/> in <paramref name="clip"/>, within it.</summary>
    private static long FrameAt(AudioClip clip, TimeSpan position) => Math.Clamp((long)(position.TotalSeconds * clip.SampleRate), 0, clip.FrameCount);

    /// <summary>The clip's samples as floats from <see cref="Frame"/> on; silence after its end, which it tells once.</summary>
    private sealed class ClipSource(AudioClip clip) : ISampleProvider
    {
        private long frame;

        /// <summary>The clip.</summary>
        public AudioClip Clip { get; } = clip;

        /// <summary>The next frame to read; set to seek.</summary>
        public long Frame
        {
            get => Interlocked.Read(ref frame);
            set => Interlocked.Exchange(ref frame, value);
        }

        /// <summary>Called once when reading reaches the end; null for none.</summary>
        public Action? EndReached { get; set; }

        /// <summary>The clip's rate and channels, as floats.</summary>
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, clip.ChannelCount);

        /// <summary>Read the next samples, and silence after the end, so the speaker keeps going until closed.</summary>
        public int Read(Span<float> destination)
        {
            // The clip's samples that remain
            int channels = Clip.ChannelCount;
            long start = Frame;
            int frames = (int)Math.Clamp(Clip.FrameCount - start, 0, destination.Length / channels);
            ReadOnlySpan<short> samples = Clip.Samples.AsSpan((int)(start * channels), frames * channels);
            for (int i = 0; i < samples.Length; i++) destination[i] = samples[i] / 32768f;
            destination[samples.Length..].Clear();
            Interlocked.CompareExchange(ref frame, start + frames, start);

            // At the end, tell once
            if (frames == 0 && start >= Clip.FrameCount && EndReached is Action reached)
            {
                EndReached = null;
                reached();
            }
            // Return the whole buffer, silence included
            return destination.Length - destination.Length % channels;
        }
    }
}
