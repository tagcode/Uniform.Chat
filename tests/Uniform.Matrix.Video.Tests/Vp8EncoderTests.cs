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
