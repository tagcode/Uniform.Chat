namespace Uniform.Matrix.Calls;

/// <summary>A camera with its VP8 encoder, as a platform gives it for a call's video, e.g. Android's camera and hardware encoder; started when the call connects, disposed when it ends.</summary>
public interface IVideoCamera : IAsyncDisposable
{
    /// <summary>Raised with each encoded VP8 frame, the first a keyframe; on any thread.</summary>
    event Action<byte[]>? FrameEncoded;

    /// <summary>Raised with small pictures of what the camera sees, for a preview, a few per second; on any thread. Cameras without a preview never raise it.</summary>
    event Action<Uniform.Matrix.Video.VideoFrame>? Previewed { add { } remove { } }

    /// <summary>Stop capturing, e.g. when the user turns the camera off, or start again; nothing where the camera cannot.</summary>
    void SetPaused(bool paused) { }

    /// <summary>Clockwise rotation in degrees that shows the camera's pictures upright, 0, 90, 180 or 270, as the device is held now.</summary>
    int Rotation => 0;

    /// <summary>Whether to turn the pictures upright before encoding, as the connection cannot tell the receiver their <see cref="Rotation"/>, e.g. through the SFU; set before <see cref="Start"/>. Cameras whose pictures are upright already ignore it.</summary>
    bool Upright { get => false; set { } }

    /// <summary>Start capturing and encoding.</summary>
    void Start();

    /// <summary>Make the next frame a keyframe, e.g. when the peer lost packets.</summary>
    void RequestKeyframe();

    /// <summary>Send at most <paramref name="bitsPerSecond"/>, as the peer asked, e.g. with a lower resolution and frame rate; nothing where the camera cannot.</summary>
    void LimitBitrate(long bitsPerSecond) { }

    /// <summary>Whether the device has another camera to switch to.</summary>
    bool CanSwitch => false;

    /// <summary>Switch to the device's next camera, e.g. from the front to the back; nothing with one camera.</summary>
    void SwitchCamera() { }

    /// <summary>The device's cameras to choose from, e.g. the front and the back camera; empty where it cannot tell.</summary>
    IReadOnlyList<CameraChoice> Cameras => [];

    /// <summary>The <see cref="CameraChoice.Id"/> of the camera in use, or wanted before <see cref="Start"/>; null where the camera cannot tell.</summary>
    string? CameraId => null;

    /// <summary>Use the camera of <paramref name="id"/>: before <see cref="Start"/> the one to start with, after it a switch; nothing for an unknown id.</summary>
    void SelectCamera(string id) { }
}

/// <summary>A camera of the device, as people choose it.</summary>
/// <param name="Id">What tells it apart and stays the same across runs, e.g. Camera2's camera id or <c>/dev/video0</c>.</param>
/// <param name="Name">Its name for people, e.g. <c>Front camera</c>.</param>
public sealed record CameraChoice(string Id, string Name);
