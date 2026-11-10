namespace Uniform.Matrix.Video;

/// <summary>Decodes a stream's frames, one at a time, in order: <see cref="Vp8.Vp8Decoder"/> in C#, or a platform's, e.g. Android's hardware decoder.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Decode one frame; return the picture to show, or null for none yet.</summary>
    /// <exception cref="InvalidDataException">When the frame cannot be decoded.</exception>
    VideoFrame? Decode(ReadOnlySpan<byte> frame);
}
