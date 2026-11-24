namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Headless.XUnit;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Video;
using static Uniform.Chat.Avalonia.Tests.AudioMessageTests;
using static Uniform.Chat.Avalonia.Tests.ChatFlowTests;

/// <summary>Video plays in the chat: WebM in C# on the desktop, its pictures on the message, with its sound keeping the time.</summary>
public class VideoMessageTests
{
    /// <summary>Folder of the test files, beside the test assembly.</summary>
    private static string Resources => Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>A VP8 and Opus WebM opens with its first picture, plays its pictures in time with its sound to the end, and shows the picture where it is moved to.</summary>
    [Fact]
    public async Task PlayWebm()
    {
        // Arrange: opened on a speaker that pulls in real time
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PlayingDevices devices = new();
        await using WebmPlayback playback = new(devices.OpenMediaSpeaker);
        List<VideoFrame> pictures = [];
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.PictureReady += picture => { lock (pictures) pictures.Add(picture); };
        playback.Ended += () => ended.TrySetResult();
        await playback.OpenAsync(Path.Combine(Resources, "vp8_opus.webm"), cancellationToken);

        // Assert: the first picture, the length
        Assert.Single(pictures);
        Assert.Equal((320, 240), (pictures[0].Width, pictures[0].Height));
        Assert.InRange(playback.Duration.TotalSeconds, 0.95, 1.05);

        // Act and assert: plays its pictures, with the sound's time, to the end
        await playback.PlayAsync();
        Assert.True(playback.IsPlaying);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.False(playback.IsPlaying);
        lock (pictures) Assert.InRange(pictures.Count, 15, 26);
        Assert.Equal(1, devices.MediaSpeakers);

        // Act and assert: moved to the middle, its picture shows
        int shown = pictures.Count;
        await playback.SeekAsync(TimeSpan.FromSeconds(0.5));
        Assert.Equal(shown + 1, pictures.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.5), playback.Position);
    }

    /// <summary>A video without sound keeps its own time to the end; another codec is not played.</summary>
    [Fact]
    public async Task PlaySilentAndRefuseOthers()
    {
        // Arrange: a silent VP8 video
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebmPlayback silent = new(() => throw new InvalidOperationException("no sound to play"));
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        silent.Ended += () => ended.TrySetResult();
        await silent.OpenAsync(Path.Combine(Resources, "vp8_silent.webm"), cancellationToken);

        // Act and assert: ends by its clock
        await silent.PlayAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(silent.Duration, silent.Position);

        // Done: VP9 is not played here
        await using WebmPlayback vp9 = new(() => throw new InvalidOperationException());
        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(() => vp9.OpenAsync(Path.Combine(Resources, "vp9.webm"), cancellationToken));
        Assert.Contains("VP9", refused.Message);
    }

    /// <summary>Bob's WebM video message plays in Alice's chat: its pictures on the message, to the end, which leaves the last picture.</summary>
    [AvaloniaFact]
    public async Task PlayVideoMessage()
    {
        // Arrange: Bob's video in a room with Alice
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Video", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await using (Stream video = File.OpenRead(Path.Combine(Resources, "vp8_opus.webm"))) await bob.SendFileAsync(roomId, video, "clip.webm", "video/webm");
        PlayingDevices devices = new();
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName) with { Audio = _ => devices }, new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == roomId);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.IsVideo), "the video message");
            MessageViewModel clip = chat.Messages.Items.First(m => m.IsVideo);
            Assert.True(clip.HasPlayer);
            Assert.False(clip.HasVideo);

            // Act
            await chat.TogglePlayAsync(clip);

            // Assert: its pictures show while it plays, and the last stays at the end
            Assert.True(clip.IsPlaying);
            Assert.True(clip.HasVideo);
            Assert.InRange(clip.PlayLength, 0.95, 1.05);
            await WaitUntilAsync(() => clip.Video?.Latest is not null && clip.PlayPosition > 0.2, "pictures");
            await CaptureAsync(model, "video-playing.png");
            await WaitUntilAsync(() => !clip.IsPlaying, "the end");
            Assert.NotNull(clip.Video!.Latest);
            Assert.False(clip.IsListened);
        }
        finally
        {
            await model.StopAsync();
        }
    }
}
