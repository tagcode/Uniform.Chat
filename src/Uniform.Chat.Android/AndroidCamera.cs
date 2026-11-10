namespace Uniform.Chat.Android;

using global::Android.Content;
using global::Android.Hardware.Camera2;
using global::Android.Media;
using global::Android.OS;
using global::Android.Views;
using Uniform.Matrix.Calls;

/// <summary>The front camera with Android's VP8 encoder, for a call's video: Camera2 renders into the encoder's input surface, and each encoded frame comes in <see cref="FrameEncoded"/>.</summary>
/// <remarks>The picture is the sensor's, landscape 640x480 at up to 30 frames per second, or 320x240 at 10 when the peer asks for less, as a slow receiver does; the encoder makes a keyframe every two seconds and when asked.</remarks>
/// <param name="context">The application.</param>
public sealed class AndroidCamera(Context context) : IVideoCamera
{
    private const string Vp8 = "video/x-vnd.on2.vp8";
    /// <summary>Bitrate below which a peer's limit makes the camera fall back to <see cref="Low"/>.</summary>
    private const long FallbackBelow = 400_000;
    /// <summary>The normal quality.</summary>
    private static readonly Quality Normal = new(640, 480, 30, 800_000);
    /// <summary>The fallback for slow receivers, which can decode it in C#.</summary>
    private static readonly Quality Low = new(320, 240, 10, 250_000);
    private Quality quality = Normal;
    private HandlerThread? thread;
    private Handler? handler;
    private MediaCodec? encoder;
    private Surface? input;
    private CameraDevice? device;
    private CameraCaptureSession? session;
    private string[] cameras = [];
    private int current;
    private CameraManager? manager;
    private bool switching;
    private bool retried;
    private bool disposed;

    /// <inheritdoc />
    public event Action<byte[]>? FrameEncoded;

    /// <summary>Start the encoder, then open the front camera into it.</summary>
    public void Start()
    {
        // A thread for the camera's and the encoder's callbacks
        thread = new HandlerThread("camera");
        thread.Start();
        handler = new Handler(thread.Looper!);

        // The encoder, fed by a surface
        StartEncoder();

        // The front camera, or the first
        CameraManager cameraManager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        manager = cameraManager;
        cameras = cameraManager.GetCameraIdList();
        current = Math.Max(0, Array.FindIndex(cameras, i => cameraManager.GetCameraCharacteristics(i).Get(CameraCharacteristics.LensFacing!) is Java.Lang.Integer facing && facing.IntValue() == (int)LensFacing.Front));
        // Done: the session starts when it opens
        Open();
    }

    /// <summary>Start a new encoder with a new input surface, as a surface that one camera wrote to may not take another on older Android versions.</summary>
    private void StartEncoder()
    {
        // VP8 at the quality's size, fed by its surface
        MediaFormat format = MediaFormat.CreateVideoFormat(Vp8, quality.Width, quality.Height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)global::Android.Media.MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, quality.Bitrate);
        format.SetInteger(MediaFormat.KeyFrameRate, quality.FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        encoder = MediaCodec.CreateEncoderByType(Vp8);
        encoder.SetCallback(new EncoderCallback(this), handler);
        encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        input = encoder.CreateInputSurface();
        // Done
        encoder.Start();
    }

    /// <summary>Stop and release the encoder and its surface.</summary>
    private void StopEncoder()
    {
        // Stopped, then released
        try { encoder?.Stop(); }
        catch (Java.Lang.IllegalStateException) { }
        encoder?.Release();
        input?.Release();
        encoder = null;
        // Done
        input = null;
    }

    /// <summary>Close the camera, and once it is closed open the next one into a new encoder, on the camera thread; presses during a switch are ignored, as slow cameras take a while to close.</summary>
    public void SwitchCamera()
    {
        // More than one camera, one switch at a time
        if (cameras.Length < 2 || handler is null) return;
        handler.Post(() =>
        {
            if (switching || disposed) return;
            current = (current + 1) % cameras.Length;
            if (device is not CameraDevice open)
            {
                Open();
                return;
            }
            // Done: the next opens when this one has closed
            switching = true;
            session?.Close();
            session = null;
            open.Close();
        });
    }

    /// <summary>Open the current camera; on failure, log it and leave the call without video.</summary>
    private void Open()
    {
        // Into the device callback
        try { manager!.OpenCamera(cameras[current], new DeviceCallback(this), handler); }
        catch (Exception ex) when (ex is CameraAccessException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            global::Android.Util.Log.Warn("UniformChat", $"Camera {cameras[current]} did not open: {ex.Message}");
        }
    }

    /// <summary>A camera closed: open the next when switching.</summary>
    private void OnClosed(CameraDevice closed)
    {
        // Only the camera this one closed
        if (!ReferenceEquals(closed, device)) return;
        device = null;
        if (!switching || disposed) return;
        switching = false;
        retried = false;
        StopEncoder();
        StartEncoder();
        // Done: a fresh surface for the next camera, as one camera's surface may not take another on older Android versions; the new encoder starts with a keyframe
        Open();
    }

    /// <summary>Ask the encoder for a keyframe next.</summary>
    public void RequestKeyframe()
    {
        // A sync frame now
        Bundle parameters = new();
        parameters.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
        // Done
        try { encoder?.SetParameters(parameters); }
        catch (Java.Lang.IllegalStateException) { }
    }

    /// <summary>Close the camera and the encoder.</summary>
    public ValueTask DisposeAsync()
    {
        // Camera first, so nothing renders into a released surface
        disposed = true;
        try
        {
            session?.Close();
            device?.Close();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already closed
        }
        StopEncoder();
        thread?.QuitSafely();
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Start repeating captures into the encoder's surface; a camera that fails, as one still closing may, is tried once more.</summary>
    private void OnOpened(CameraDevice opened)
    {
        // A session with the one target
        device = opened;
        if (disposed)
        {
            opened.Close();
            return;
        }
        try
        {
#pragma warning disable CA1422 // The surface list form is enough here and works on every version
            opened.CreateCaptureSession([input!], new SessionCallback(this), handler);
#pragma warning restore CA1422
        }
        catch (Exception ex) when (ex is CameraAccessException or Java.Lang.IllegalStateException or Java.Lang.IllegalArgumentException)
        {
            // On a camera that went away: once more, a moment later
            Fail(opened, ex);
        }
    }

    /// <summary>Capture continuously, as for recording.</summary>
    private void OnConfigured(CameraCaptureSession configured)
    {
        // The recording template keeps the frame rate steady
        session = configured;
        try
        {
            CaptureRequest.Builder request = device!.CreateCaptureRequest(CameraTemplate.Record);
            request.AddTarget(input!);
            if (FrameRateRange() is global::Android.Util.Range range) request.Set(CaptureRequest.ControlAeTargetFpsRange!, range);
            configured.SetRepeatingRequest(request.Build(), null, handler);
        }
        catch (Exception ex) when (ex is CameraAccessException or Java.Lang.IllegalStateException or Java.Lang.IllegalArgumentException)
        {
            // On a camera that went away
            if (device is CameraDevice failed) Fail(failed, ex);
        }
    }

    /// <summary>Fall back to 320x240 at 10 pictures per second when the peer asks for less than <see cref="FallbackBelow"/>, as a slow receiver does: a new encoder at that size, and the open camera's session moved to it.</summary>
    public void LimitBitrate(long bitsPerSecond)
    {
        // Once, on the camera thread
        if (bitsPerSecond >= FallbackBelow || handler is null) return;
        handler.Post(() =>
        {
            if (quality == Low || disposed) return;
            quality = Low;
            global::Android.Util.Log.Info("UniformChat", $"Video falls back to {Low.Width}x{Low.Height} at {Low.FrameRate} per second, as the peer asked for {bitsPerSecond} bit/s");
            session?.Close();
            session = null;
            StopEncoder();
            StartEncoder();
            // Done: the open camera into the new surface
            if (device is CameraDevice open) OnOpened(open);
        });
    }

    /// <summary>A size, frame rate and bitrate of the video sent.</summary>
    private sealed record Quality(int Width, int Height, int FrameRate, int Bitrate);

    /// <summary>Return the camera's frame rate range closest to the quality's frame rate: the highest at or below it, else the lowest; null when the camera tells none.</summary>
    private global::Android.Util.Range? FrameRateRange()
    {
        // At or below the wanted rate preferred
        if (manager?.GetCameraCharacteristics(cameras[current]).Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges!) is not Java.Lang.Object value) return null;
        global::Android.Util.Range[] all = global::Android.Runtime.JNIEnv.GetArray<global::Android.Util.Range>(value.Handle) ?? [];
        static int Upper(global::Android.Util.Range r) => ((Java.Lang.Integer)r.Upper!).IntValue();
        // Return range
        return all.Where(r => Upper(r) <= quality.FrameRate).OrderByDescending(Upper).FirstOrDefault() ?? all.OrderBy(Upper).FirstOrDefault();
    }

    /// <summary>Log a camera's failure and close it; open it once more after a second, unless already tried.</summary>
    private void Fail(CameraDevice camera, Exception ex)
    {
        // Logged, closed, and maybe retried
        global::Android.Util.Log.Warn("UniformChat", $"Camera failed: {ex.Message}");
        camera.Close();
        device = null;
        if (retried || disposed) return;
        retried = true;
        // Done
        handler?.PostDelayed(Open, 1000);
    }

    /// <summary>Give an encoded frame; the codec's own configuration data is not a frame.</summary>
    private void OnEncoded(MediaCodec codec, int index, MediaCodec.BufferInfo info)
    {
        // The frame's bytes, then the buffer back
        try
        {
            if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0 || info.Size == 0) return;
            Java.Nio.ByteBuffer buffer = codec.GetOutputBuffer(index)!;
            byte[] frame = new byte[info.Size];
            buffer.Position(info.Offset);
            buffer.Get(frame, 0, info.Size);
            FrameEncoded?.Invoke(frame);
        }
        finally
        {
            codec.ReleaseOutputBuffer(index, false);
        }
    }

    /// <summary>Opens the session when the camera opens.</summary>
    private sealed class DeviceCallback(AndroidCamera owner) : CameraDevice.StateCallback
    {
        /// <inheritdoc />
        public override void OnOpened(CameraDevice camera) => owner.OnOpened(camera);

        /// <inheritdoc />
        public override void OnDisconnected(CameraDevice camera) => camera.Close();

        /// <inheritdoc />
        public override void OnClosed(CameraDevice camera) => owner.OnClosed(camera);

        /// <inheritdoc />
        public override void OnError(CameraDevice camera, CameraError error) => camera.Close();
    }

    /// <summary>Starts capturing when the session is configured.</summary>
    private sealed class SessionCallback(AndroidCamera owner) : CameraCaptureSession.StateCallback
    {
        /// <inheritdoc />
        public override void OnConfigured(CameraCaptureSession session) => owner.OnConfigured(session);

        /// <inheritdoc />
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            // Logged, so a camera that cannot write into the encoder shows
            global::Android.Util.Log.Warn("UniformChat", "Camera session could not be configured");
            session.Close();
        }
    }

    /// <summary>Passes encoded frames on; the input comes from the surface.</summary>
    private sealed class EncoderCallback(AndroidCamera owner) : MediaCodec.Callback
    {
        /// <inheritdoc />
        public override void OnInputBufferAvailable(MediaCodec codec, int index) { }

        /// <inheritdoc />
        public override void OnOutputBufferAvailable(MediaCodec codec, int index, MediaCodec.BufferInfo info) => owner.OnEncoded(codec, index, info);

        /// <inheritdoc />
        public override void OnError(MediaCodec codec, MediaCodec.CodecException e) { }

        /// <inheritdoc />
        public override void OnOutputFormatChanged(MediaCodec codec, MediaFormat format) { }
    }
}
