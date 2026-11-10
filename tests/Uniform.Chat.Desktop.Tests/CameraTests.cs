namespace Uniform.Chat.Desktop.Tests;

using Uniform.Chat.Desktop.Cameras;
using Uniform.Matrix.Video;

/// <summary>The desktop cameras: their pixel formats converted to I420, and on Windows Media Foundation's start and its list of cameras.</summary>
public class CameraTests
{
    /// <summary>YUYV, two pixels in four bytes, gives each pixel's luma and every other row's chroma, at full and half size.</summary>
    [Fact]
    public void ConvertYuyv()
    {
        // Arrange: 4x2, luma 10 to 17, chroma by pair and row
        byte[] yuyv = [10, 100, 11, 200, 12, 101, 13, 201, 14, 102, 15, 202, 16, 103, 17, 203];

        // Act
        VideoFrame full = V4l2Camera.ToI420(yuyv, 4, 2, half: false);
        VideoFrame half = V4l2Camera.ToI420(yuyv, 4, 2, half: true);

        // Assert
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 }, full.Y);
        Assert.Equal(new byte[] { 100, 101 }, full.U);
        Assert.Equal(new byte[] { 200, 201 }, full.V);
        Assert.Equal((2, 1), (half.Width, half.Height));
        // Done
        Assert.Equal(new byte[] { 10, 12 }, half.Y);
    }

    /// <summary>NV12, a luma plane and then interleaved U and V, gives the planes apart, at full and half size.</summary>
    [Fact]
    public void ConvertNv12()
    {
        // Arrange: 4x2, then one row of two U-V pairs
        byte[] nv12 = [10, 11, 12, 13, 14, 15, 16, 17, 100, 200, 101, 201];

        // Act
        VideoFrame full = MediaFoundationCamera.ToI420(nv12, 4, 2, half: false);
        VideoFrame half = MediaFoundationCamera.ToI420(nv12, 4, 2, half: true);

        // Assert
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 }, full.Y);
        Assert.Equal(new byte[] { 100, 101 }, full.U);
        Assert.Equal(new byte[] { 200, 201 }, full.V);
        Assert.Equal((2, 1), (half.Width, half.Height));
        Assert.Equal(new byte[] { 10, 12 }, half.Y);
        // Done
        Assert.Equal(new byte[] { 100 }, half.U);
    }

    /// <summary>On Windows, Media Foundation starts and lists its cameras without failing, none on a machine without; a camera there opens and closes. Elsewhere skipped.</summary>
    [Fact]
    public async Task ListWindowsCameras()
    {
        // Windows only
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Media Foundation is on Windows only.");
            return;
        }

        // Act: the list, and a camera when there is one
        bool available = MediaFoundationCamera.Available();
        if (!available) return;
        MediaFoundationCamera camera = new();
        camera.Start();
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        // Done
        await camera.DisposeAsync();
    }
}
