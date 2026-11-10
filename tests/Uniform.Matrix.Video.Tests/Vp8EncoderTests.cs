namespace Uniform.Matrix.Video.Tests;

using Uniform.Matrix.Video.Vp8;

/// <summary>The VP8 encoder: its frames decode, bit for bit, to the picture it reconstructed, close to the source, and smaller when coarser.</summary>
public class Vp8EncoderTests
{
    /// <summary>A picture of a size that is not whole macroblocks decodes to the encoder's reconstruction exactly, at good quality.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(100)]
    public void DecodeOwnKeyframe(int quantizer)
    {
        // Arrange: gradients, a square and noise
        VideoFrame source = Picture(100, 60, seed: quantizer);
        Vp8Encoder encoder = new(quantizer);

        // Act
        byte[] frame = encoder.Encode(source);
        VideoFrame decoded = new Vp8Decoder().Decode(frame)!;

        // Assert: the size, the decoder's picture as the encoder's, and close to the source
        VideoFrame reconstruction = encoder.Reconstruction;
        Assert.Equal((100, 60), (decoded.Width, decoded.Height));
        Assert.Equal(reconstruction.Y, decoded.Y);
        Assert.Equal(reconstruction.U, decoded.U);
        Assert.Equal(reconstruction.V, decoded.V);
        // Done
        Assert.True(Psnr(source.Y, decoded.Y) > (quantizer < 50 ? 30 : 24), $"PSNR {Psnr(source.Y, decoded.Y):F1} dB at q {quantizer}, {frame.Length} bytes");
    }

    /// <summary>A moving square over a still background: inter frames between keyframes decode, bit for bit, to the encoder's pictures, and cost a fraction of a keyframe; a requested keyframe comes next.</summary>
    [Fact]
    public void DecodeOwnInterFrames()
    {
        // Arrange: a keyframe every 10 frames
        Vp8Encoder encoder = new(40) { KeyframeInterval = 10 };
        Vp8Decoder decoder = new();
        VideoFrame background = Picture(160, 120, seed: 3);
        List<(bool Keyframe, int Size)> frames = [];

        // Act: 25 frames, a keyframe asked for before the 23rd
        for (int i = 0; i < 25; i++)
        {
            if (i == 22) encoder.RequestKeyframe();
            byte[] frame = encoder.Encode(Moved(background, i));
            VideoFrame decoded = decoder.Decode(frame)!;
            VideoFrame reconstruction = encoder.Reconstruction;
            Assert.Equal(reconstruction.Y, decoded.Y);
            Assert.Equal(reconstruction.U, decoded.U);
            Assert.Equal(reconstruction.V, decoded.V);
            frames.Add(((frame[0] & 1) == 0, frame.Length));
        }

        // Assert: keyframes where due and asked, and inter frames far smaller
        Assert.Equal([0, 10, 20, 22], frames.Select((f, i) => (f, i)).Where(x => x.f.Keyframe).Select(x => x.i));
        double keyframe = frames.Where(f => f.Keyframe).Average(f => f.Size), inter = frames.Where(f => !f.Keyframe).Average(f => f.Size);
        // Done
        Assert.True(inter * 3 < keyframe, $"inter frames {inter:F0} bytes, keyframes {keyframe:F0}");
    }

    /// <summary>A picture panning by two pixels a frame: with motion search, inter frames decode bit for bit and are much smaller than without.</summary>
    [Fact]
    public void FollowMotion()
    {
        // Arrange: a textured scene, and both encoders
        VideoFrame scene = Picture(400, 300, seed: 5);
        Vp8Encoder searching = new(40), still = new(40) { MotionSearch = false };
        Vp8Decoder decoder = new();
        long searchedBytes = 0, stillBytes = 0;

        // Act: 10 frames, each a 160x120 window moved 2 pixels right and 2 down
        for (int i = 0; i < 10; i++)
        {
            VideoFrame window = Crop(scene, 20 + 2 * i, 20 + 2 * i, 160, 120);
            byte[] frame = searching.Encode(window);
            VideoFrame decoded = decoder.Decode(frame)!;
            Assert.Equal(searching.Reconstruction.Y, decoded.Y);
            Assert.Equal(searching.Reconstruction.U, decoded.U);
            Assert.Equal(searching.Reconstruction.V, decoded.V);
            long other = still.Encode(window).Length;
            if (i == 0) continue;
            searchedBytes += frame.Length;
            stillBytes += other;
        }

        // Done
        Assert.True(searchedBytes * 2 < stillBytes, $"with motion {searchedBytes} bytes, without {stillBytes}");
    }

    /// <summary>Return the <paramref name="width"/> by <paramref name="height"/> window of a picture at (<paramref name="x"/>, <paramref name="y"/>), both even.</summary>
    private static VideoFrame Crop(VideoFrame picture, int x, int y, int width, int height)
    {
        // Rows of each plane
        byte[] cy = new byte[width * height], cu = new byte[width / 2 * (height / 2)], cv = new byte[width / 2 * (height / 2)];
        for (int r = 0; r < height; r++) picture.Y.AsSpan((y + r) * picture.Width + x, width).CopyTo(cy.AsSpan(r * width));
        for (int r = 0; r < height / 2; r++)
        {
            picture.U.AsSpan((y / 2 + r) * picture.ChromaWidth + x / 2, width / 2).CopyTo(cu.AsSpan(r * width / 2));
            picture.V.AsSpan((y / 2 + r) * picture.ChromaWidth + x / 2, width / 2).CopyTo(cv.AsSpan(r * width / 2));
        }
        // Return window
        return new VideoFrame(width, height, cy, cu, cv);
    }

    /// <summary>Return <paramref name="background"/> with a light square moved by <paramref name="index"/>.</summary>
    private static VideoFrame Moved(VideoFrame background, int index)
    {
        // A copy of the luma with the square
        byte[] y = (byte[])background.Y.Clone();
        int left = index * 3 % (background.Width - 20);
        for (int r = 40; r < 60; r++) y.AsSpan(r * background.Width + left, 20).Fill(230);
        // Return picture
        return new VideoFrame(background.Width, background.Height, y, background.U, background.V);
    }

    /// <summary>A coarser quantizer makes a smaller frame.</summary>
    [Fact]
    public void ShrinkWhenCoarser()
    {
        // Arrange
        VideoFrame source = Picture(320, 240, seed: 1);
        // Done
        Assert.True(new Vp8Encoder(80).Encode(source).Length < new Vp8Encoder(20).Encode(source).Length);
    }

    /// <summary>Return a test picture: smooth gradients, a sharp square and some noise.</summary>
    private static VideoFrame Picture(int width, int height, int seed)
    {
        // Luma and chroma planes
        Random random = new(seed);
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        byte[] y = new byte[width * height], u = new byte[cw * ch], v = new byte[cw * ch];
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                bool square = r > height / 4 && r < height / 2 && c > width / 3 && c < width / 2;
                y[r * width + c] = (byte)Math.Clamp((square ? 230 : c * 200 / width + r * 40 / height) + random.Next(-6, 7), 0, 255);
            }
        }
        for (int i = 0; i < u.Length; i++)
        {
            u[i] = (byte)(100 + i % cw);
            v[i] = (byte)(160 - i / cw);
        }
        // Return picture
        return new VideoFrame(width, height, y, u, v);
    }

    /// <summary>Return the peak signal-to-noise ratio of two planes, in decibels.</summary>
    private static double Psnr(byte[] a, byte[] b)
    {
        // Mean squared error
        double error = 0;
        for (int i = 0; i < a.Length; i++) error += (a[i] - b[i]) * (a[i] - b[i]);
        error /= a.Length;
        // Return ratio
        return error == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / error);
    }
}
