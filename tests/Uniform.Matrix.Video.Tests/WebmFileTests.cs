namespace Uniform.Matrix.Video.Tests;

using Uniform.Matrix.Video.Vp8;
using Uniform.Matrix.Video.Webm;

/// <summary>Tests of <see cref="WebmFile"/>: files ffmpeg made, as written and as a live recording, read into tracks and frames that decode as libvpx decodes them.</summary>
public class WebmFileTests
{
    /// <summary>Folder of the test files, beside the test assembly.</summary>
    private static string Resources => Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>A VP8 and Opus file has its two tracks, 25 pictures at 25 per second with a keyframe every 10, and Opus packets to its one second.</summary>
    [Fact]
    public void ReadTracksAndFrames()
    {
        // Act
        WebmFile file = WebmFile.Parse(Read("vp8_opus.webm"));

        // Assert: tracks
        WebmTrack video = Assert.Single(file.Tracks, t => t.Kind == WebmTrackKind.Video);
        WebmTrack audio = Assert.Single(file.Tracks, t => t.Kind == WebmTrackKind.Audio);
        Assert.Equal(("V_VP8", 320, 240), (video.CodecId, video.Width, video.Height));
        Assert.Equal(("A_OPUS", 48000.0, 2), (audio.CodecId, audio.SampleRate, audio.Channels));
        Assert.StartsWith("OpusHead", System.Text.Encoding.ASCII.GetString(audio.CodecPrivate!));

        // Assert: pictures, keyframes and times; audio up to the end
        WebmFrame[] pictures = [.. file.Frames.Where(f => f.Track == video.Number)];
        Assert.Equal(25, pictures.Length);
        Assert.Equal([0, 10, 20], pictures.Select((f, i) => (f, i)).Where(p => p.f.IsKeyframe).Select(p => p.i));
        Assert.Equal(TimeSpan.FromMilliseconds(40), pictures[1].Timestamp);
        Assert.InRange(file.Frames.Count(f => f.Track == audio.Number), 45, 55);
        Assert.InRange(file.Duration.TotalSeconds, 0.95, 1.05);
    }

    /// <summary>The live recording, with a segment of unknown size and short clusters, as browsers write, has the same frames.</summary>
    [Fact]
    public void ReadLiveRecording()
    {
        // Act
        WebmFile written = WebmFile.Parse(Read("vp8_opus.webm"));
        WebmFile live = WebmFile.Parse(Read("vp8_opus_live.webm"));

        // Assert
        Assert.Equal(written.Frames.Count, live.Frames.Count);
        Assert.Equal(written.Frames.Select(f => (f.Timestamp, f.IsKeyframe, f.Data.Length)), live.Frames.Select(f => (f.Timestamp, f.IsKeyframe, f.Data.Length)));
        Assert.InRange(live.Duration.TotalSeconds, 0.9, 1.05);
    }

    /// <summary>The VP8 frames decode in order with <see cref="Vp8Decoder"/> to libvpx's pictures, the first and the last.</summary>
    [Fact]
    public void DecodeLikeLibvpx()
    {
        // Decode every picture
        WebmFile file = WebmFile.Parse(Read("vp8_opus.webm"));
        int track = file.Tracks.First(t => t.Kind == WebmTrackKind.Video).Number;
        using Vp8Decoder decoder = new();
        List<VideoFrame> pictures = [];
        foreach (WebmFrame frame in file.Frames.Where(f => f.Track == track)) if (decoder.Decode(frame.Data.Span) is VideoFrame picture) pictures.Add(picture);

        // Done: the same pixels
        Assert.Equal(25, pictures.Count);
        AssertSame(Read("vp8_opus.first.yuv"), pictures[0]);
        AssertSame(Read("vp8_opus.last.yuv"), pictures[^1]);
    }

    /// <summary>A VP9 file is read, with its codec named; data that is not WebM fails as such, and a file cut short keeps the frames before the cut.</summary>
    [Fact]
    public void ReadOtherFiles()
    {
        // Another codec
        Assert.Equal("V_VP9", WebmFile.Parse(Read("vp9.webm")).Tracks.Single().CodecId);

        // Done: not WebM, and cut in half
        Assert.Throws<InvalidDataException>(() => WebmFile.Parse(new byte[100]));
        byte[] data = Read("vp8_opus.webm");
        WebmFile cut = WebmFile.Parse(data[..(data.Length / 2)]);
        Assert.InRange(cut.Frames.Count, 1, WebmFile.Parse(data).Frames.Count - 1);
    }

    /// <summary>Return the bytes of a test file.</summary>
    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Resources, name));

    /// <summary>Assert that a picture has the planes of a raw I420 reference.</summary>
    private static void AssertSame(byte[] reference, VideoFrame picture)
    {
        // Each plane
        int luma = picture.Width * picture.Height, chroma = picture.U.Length;
        Assert.Equal(reference[..luma], picture.Y);
        Assert.Equal(reference[luma..(luma + chroma)], picture.U);
        Assert.Equal(reference[(luma + chroma)..], picture.V);
    }
}
