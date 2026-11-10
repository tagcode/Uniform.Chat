namespace Uniform.Matrix.Calls;

/// <summary>A camera with its VP8 encoder, as a platform gives it for a call's video, e.g. Android's camera and hardware encoder; started when the call connects, disposed when it ends.</summary>
public interface IVideoCamera : IAsyncDisposable
{
    /// <summary>Raised with each encoded VP8 frame, the first a keyframe; on any thread.</summary>
    event Action<byte[]>? FrameEncoded;

    /// <summary>Start capturing and encoding.</summary>
    void Start();

    /// <summary>Make the next frame a keyframe, e.g. when the peer lost packets.</summary>
    void RequestKeyframe();
}
