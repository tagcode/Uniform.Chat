namespace Uniform.Chat.Desktop.Tests;

using NAudio.Wave;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Desktop.Video;
using Uniform.Matrix.Video;

/// <summary>Tests of video through the desktops' decoders, GStreamer on Linux and Media Foundation on Windows: an MP4 with H.264 and AAC as phones record it, pictures upright and small, sound decoded, played to its end; skipped off their platform and where they cannot decode H.264.</summary>
public class VideoTests
{
    /// <summary>Folder of the test files, beside the test assembly.</summary>
    private static string Resources => Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>The decoders tested, each skipped off its platform.</summary>
    public static TheoryData<string> Decoders => ["GStreamer", "MediaFoundation"];

    /// <summary>The MP4, 322×180 with its rows padded in the decoder's layout, dark on the left, and tagged to turn a quarter counter-clockwise, gives upright 180×322 pictures, dark at the bottom, 25 a second from time 0 despite its edit list, and two seconds of sound.</summary>
    [Theory]
    [MemberData(nameof(Decoders))]
    public void DecodeMp4(string decoder)
    {
        // Arrange
        Func<string, IVideoSource> open = Decoder(decoder);

        // Act
        using IVideoSource source = open(Path.Combine(Resources, "halves.mp4"));
        List<(VideoFrame Picture, TimeSpan Time)> pictures = [];
        for (int i = 0; i < 500 && !source.IsAtEnd; i++) if (source.Next() is { } next) pictures.Add(next);

        // Assert: length, sound, pictures and their times
        Assert.InRange(source.Duration.TotalSeconds, 1.95, 2.1);
        Assert.NotNull(source.Sound);
        Assert.Equal((48000, 2), (source.Sound.SampleRate, source.Sound.ChannelCount));
        Assert.InRange(source.Sound.Duration.TotalSeconds, 1.95, 2.1);
        Assert.InRange(pictures.Count, 48, 51);
        Assert.Equal(TimeSpan.Zero, pictures[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(40), pictures[1].Time - pictures[0].Time);

        // Assert: upright by its rotation, 180×322, the dark half at the bottom
        VideoFrame first = pictures[0].Picture.Rotated(pictures[0].Picture.Rotation);
        Assert.Equal((180, 322), (first.Width, first.Height));
        double top = first.Y.Take(first.Width * 100).Average(b => b), bottom = first.Y.Skip(first.Width * (first.Height - 100)).Average(b => b);
        Assert.True(top > 180 && bottom < 60, $"top {top:F0}, bottom {bottom:F0}");
    }

    /// <summary>Seeking gives the picture at the position; a file without video is refused.</summary>
    [Theory]
    [MemberData(nameof(Decoders))]
    public void SeekAndRefuse(string decoder)
    {
        // Arrange
        Func<string, IVideoSource> open = Decoder(decoder);
        using IVideoSource source = open(Path.Combine(Resources, "halves.mp4"));

        // Act
        source.SeekTo(TimeSpan.FromSeconds(1));
        (VideoFrame Picture, TimeSpan Time)? next = null;
        for (int i = 0; i < 50 && next is null; i++) next = source.Next();

        // Assert
        Assert.NotNull(next);
        Assert.InRange(next.Value.Time.TotalSeconds, 0.99, 1.05);
        Assert.Throws<NotSupportedException>(() => open(Path.Combine(Resources, "sound.m4a")));
    }

    /// <summary>The MP4 plays to its end on a speaker, its pictures as they are due.</summary>
    [Theory]
    [MemberData(nameof(Decoders))]
    public async Task PlayMp4(string decoder)
    {
        // Arrange
        Func<string, IVideoSource> open = Decoder(decoder);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using DecodedVideoPlayback playback = new(open, () => new PullingSpeaker());
        int pictures = 0;
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.PictureReady += _ => Interlocked.Increment(ref pictures);
        playback.Ended += () => ended.TrySetResult();
        await playback.OpenAsync(Path.Combine(Resources, "halves.mp4"), cancellationToken);

        // Act
        await playback.PlayAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        Assert.InRange(pictures, 40, 52);
        Assert.False(playback.IsPlaying);
    }

    /// <summary>An interface Media Foundation returns is released at once by <see cref="MediaFoundation.Release"/>, not when collected: decoders keep few samples, and the AV1 one stops after a seek's dropped pictures when they are not given back.</summary>
    [Fact]
    public void ReleaseAtOnce()
    {
        // Arrange
        if (!OperatingSystem.IsWindows() || !MediaFoundation.IsAvailable) Assert.Skip("Media Foundation is not on this machine.");
        MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out MediaFoundation.IMFMediaType type));

        // Act
        MediaFoundation.Release(type);

        // Assert: nothing left to call
        Assert.ThrowsAny<Exception>(() => type.SetUINT32(MediaFoundation.AudioChannels, 2));
    }

    /// <summary>Return the opener of a decoder; skip off its platform, or where it cannot decode H.264, as GStreamer without its plugins on build servers, or Windows N without its Media Feature Pack.</summary>
    private static Func<string, IVideoSource> Decoder(string decoder)
    {
        // The platform's decoder
        Func<string, IVideoSource>? open = decoder switch
        {
            "GStreamer" when OperatingSystem.IsLinux() && GStreamer.IsAvailable => path => new GStreamerVideoSource(path),
            "MediaFoundation" when OperatingSystem.IsWindows() && MediaFoundation.IsAvailable => path => new MediaFoundationVideoSource(path),
            _ => null,
        };
        if (open is null) Assert.Skip($"{decoder} is not on this machine.");

        // Then a decoder of the file
        try { open(Path.Combine(Resources, "halves.mp4")).Dispose(); }
        catch (NotSupportedException exception) { Assert.Skip($"{decoder} cannot decode H.264: {exception.Message}"); }
        // Return the opener
        return open;
    }

    /// <summary>Reads 20 ms at a time from its source, as a device would.</summary>
    private sealed class PullingSpeaker : IWavePlayer
    {
        private IWaveProvider? source;
        private Timer? timer;

        /// <summary>Playing or stopped.</summary>
        public PlaybackState PlaybackState { get; private set; }
        /// <summary>Unused.</summary>
        public float Volume { get; set; } = 1;
        /// <summary>The source's format.</summary>
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        /// <summary>Not raised.</summary>
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        /// <summary>Keep the source.</summary>
        public void Init(IWaveProvider waveProvider) => source = waveProvider;
        /// <summary>Pull every 20 ms.</summary>
        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            timer = new Timer(_ => { lock (this) if (PlaybackState == PlaybackState.Playing) source?.Read(new byte[960 * 4]); }, null, 0, 20);
        }
        /// <summary>Stop.</summary>
        public void Pause() => Stop();
        /// <summary>Stop.</summary>
        public void Stop()
        {
            lock (this) PlaybackState = PlaybackState.Stopped;
            timer?.Dispose();
        }
        /// <summary>Stop.</summary>
        public void Dispose() => Stop();
    }
}
