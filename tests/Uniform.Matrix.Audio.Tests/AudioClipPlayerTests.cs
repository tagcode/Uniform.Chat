namespace Uniform.Matrix.Audio.Tests;

using NAudio.Wave;
using Uniform.Matrix.Audio.Decoding;

/// <summary>Tests of <see cref="AudioClipPlayer"/> on a speaker that pulls in real time.</summary>
public class AudioClipPlayerTests
{
    /// <summary>A clip plays in the output format, advances, pauses at its position, seeks, resumes, and ends with the speaker closed.</summary>
    [Fact]
    public async Task PlayPauseSeekAndEnd()
    {
        // Arrange: a 0.6 s mono clip at 22.05 kHz
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AudioClip clip = new([.. Enumerable.Range(0, 13230).Select(i => (short)(8000 * Math.Sin(i * 0.1)))], 22050, 1);
        List<FakeSpeaker> speakers = [];
        await using AudioClipPlayer player = new(() => { FakeSpeaker speaker = new(); lock (speakers) speakers.Add(speaker); return speaker; });
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Ended += _ => ended.TrySetResult();

        // Act and assert: plays, converted, and advances
        await player.PlayAsync(clip);
        Assert.True(player.IsPlaying);
        Assert.Equal(AudioClipPlayer.OutputFormat, speakers[0].Format);
        await WaitUntilAsync(() => player.Position > TimeSpan.FromMilliseconds(100), cancellationToken);
        Assert.True(speakers[0].Energy > 0);

        // Act and assert: paused, the speaker closed, the position kept
        await player.PauseAsync();
        TimeSpan paused = player.Position;
        Assert.False(player.IsPlaying);
        Assert.True(speakers[0].Disposed);
        await Task.Delay(100, cancellationToken);
        Assert.Equal(paused, player.Position);

        // Act and assert: seek near the end, resume on a new speaker, and end
        player.Seek(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), player.Position);
        await player.ResumeAsync();
        Assert.Equal(2, speakers.Count);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.False(player.IsPlaying);
        Assert.True(speakers[1].Disposed);

        // Done: stopped forgets the clip
        await player.StopAsync();
        Assert.Null(player.Clip);
    }

    /// <summary>A speaker that does not open fails the play, without throwing.</summary>
    [Fact]
    public async Task TellWhenSpeakerFails()
    {
        // Arrange
        await using AudioClipPlayer player = new(() => throw new InvalidOperationException("no speaker"));
        Exception? failure = null;
        player.Failed += e => failure = e;

        // Act
        await player.PlayAsync(new AudioClip(new short[4800], 48000, 1));

        // Assert
        Assert.False(player.IsPlaying);
        Assert.Equal("no speaker", failure?.Message);
    }

    /// <summary>Wait until a condition holds, failing after five seconds.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        // Poll
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail("Timed out waiting.");
            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>Plays 20 ms at a time from its source, summing the energy it plays.</summary>
    private sealed class FakeSpeaker : IWavePlayer
    {
        private IWaveProvider? source;
        private Timer? timer;
        private double energy;

        /// <summary>The format it was given.</summary>
        public WaveFormat? Format => source?.WaveFormat;
        /// <summary>Sum of squared samples played.</summary>
        public double Energy => Volatile.Read(ref energy);
        /// <summary>Whether it was closed.</summary>
        public bool Disposed { get; private set; }
        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Volume, unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>The format played.</summary>
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }

        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => source = waveProvider;
        /// <summary>Play in real time.</summary>
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            timer = new Timer(_ => Pull(), null, 0, 20);
        }
        /// <summary>Pause.</summary>
        public void Pause() => Stop();
        /// <summary>Stop.</summary>
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            timer?.Dispose();
        }
        /// <summary>Close.</summary>
        public void Dispose()
        {
            Stop();
            Disposed = true;
        }

        /// <summary>Play 20 ms from the source.</summary>
        private void Pull()
        {
            // 16-bit stereo at 48 kHz
            lock (this)
            {
                if (source is null || PlaybackState != PlaybackState.Playing) return;
                byte[] buffer = new byte[960 * 4];
                int read = source.Read(buffer);
                double sum = 0;
                for (int i = 0; i + 1 < read; i += 2) sum += Math.Pow(BitConverter.ToInt16(buffer, i), 2);
                energy += sum;
            }
        }
    }
}
