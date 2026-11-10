namespace Uniform.Chat.Avalonia.Tests;

using System.Runtime.InteropServices;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Headless;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Video;

/// <summary>The call's video: <see cref="VideoView"/> sized and drawn from its <see cref="VideoFeed"/>.</summary>
public class VideoViewTests
{
    /// <summary>A 4:3 red picture fills the width of the view with its aspect kept; a new size is followed; clearing the feed hides it.</summary>
    [AvaloniaFact]
    public void ShowPictures()
    {
        // Arrange: an empty view at the top of a black window
        VideoFeed feed = new();
        VideoView view = new() { Feed = feed, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top };
        Window window = new() { Width = 400, Height = 400, Background = Brushes.Black, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, view.Bounds.Height);

        // Act: a red 64x48 picture, BT.601 limited range, from another thread as the decoder gives it
        Task.Run(() => feed.Push(Picture(64, 48, 81, 90, 240))).Wait();
        Pump(() => view.Bounds.Height == 300);

        // Assert: 400x300, red in the middle, black below
        Assert.Equal(new Size(400, 300), view.Bounds.Size);
        using (Bitmap frame = window.CaptureRenderedFrame()!)
        {
            Assert.Equal((255, 0, 0), Pixel(frame, 200, 150));
            Assert.Equal((0, 0, 0), Pixel(frame, 200, 350));
        }

        // Act and assert: a 16:9 picture, then none
        feed.Push(Picture(160, 90, 81, 90, 240));
        Pump(() => view.Bounds.Height == 225);
        Assert.Equal(new Size(400, 225), view.Bounds.Size);
        feed.Clear();
        Pump(() => view.Bounds.Height == 0);
        // Done
        Assert.Equal(0, view.Bounds.Height);
        window.Close();
    }

    /// <summary>Run the UI's jobs until <paramref name="done"/>, at most two seconds, as pictures are converted on the thread pool first.</summary>
    private static void Pump(Func<bool> done)
    {
        // Jobs, then a moment
        for (int i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        // Done
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Return a picture of one color.</summary>
    private static VideoFrame Picture(int width, int height, byte y, byte u, byte v)
    {
        // Planes filled
        int chroma = (width + 1) / 2 * ((height + 1) / 2);
        // Return picture
        return new VideoFrame(width, height, Enumerable.Repeat(y, width * height).ToArray(), Enumerable.Repeat(u, chroma).ToArray(), Enumerable.Repeat(v, chroma).ToArray());
    }

    /// <summary>Return the red, green and blue of a rendered pixel.</summary>
    private static (int R, int G, int B) Pixel(Bitmap frame, int x, int y)
    {
        // One pixel copied out, in the frame's order of channels
        byte[] pixel = new byte[4];
        GCHandle pinned = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(x, y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4); }
        finally { pinned.Free(); }
        if (frame.Format == global::Avalonia.Platform.PixelFormat.Rgba8888) return (pixel[0], pixel[1], pixel[2]);
        // Return color of BGRA
        return (pixel[2], pixel[1], pixel[0]);
    }
}
