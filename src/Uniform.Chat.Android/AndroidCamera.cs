namespace Uniform.Chat.Android;

using global::Android.Content;
using global::Android.Hardware.Camera2;
using global::Android.Media;
using global::Android.OS;
using global::Android.Views;
using Uniform.Matrix.Calls;

/// <summary>The front camera with Android's VP8 encoder, for a call's video: Camera2 renders into the encoder's input surface, and each encoded frame comes in <see cref="FrameEncoded"/>.</summary>
/// <remarks>The picture is the sensor's, landscape 640x480; the encoder makes a keyframe every two seconds and when asked.</remarks>
/// <param name="context">The application.</param>
public sealed class AndroidCamera(Context context) : IVideoCamera
{
    private const string Vp8 = "video/x-vnd.on2.vp8";
    private const int Width = 640;
    private const int Height = 480;
    private HandlerThread? thread;
    private Handler? handler;
    private MediaCodec? encoder;
    private Surface? input;
    private CameraDevice? device;
    private CameraCaptureSession? session;
    private string[] cameras = [];
    private int current;
    private CameraManager? manager;

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
        MediaFormat format = MediaFormat.CreateVideoFormat(Vp8, Width, Height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)global::Android.Media.MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, 800_000);
        format.SetInteger(MediaFormat.KeyFrameRate, 30);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        encoder = MediaCodec.CreateEncoderByType(Vp8);
        encoder.SetCallback(new EncoderCallback(this), handler);
        encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        input = encoder.CreateInputSurface();
        encoder.Start();

        // The front camera, or the first
        CameraManager cameraManager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        manager = cameraManager;
        cameras = cameraManager.GetCameraIdList();
        current = Math.Max(0, Array.FindIndex(cameras, i => cameraManager.GetCameraCharacteristics(i).Get(CameraCharacteristics.LensFacing!) is Java.Lang.Integer facing && facing.IntValue() == (int)LensFacing.Front));
        // Done: the session starts when it opens
        cameraManager.OpenCamera(cameras[current], new DeviceCallback(this), handler);
    }

    /// <summary>Close the camera and open the next one into the same encoder, on the camera thread.</summary>
    public void SwitchCamera()
    {
        // More than one camera
        if (cameras.Length < 2 || handler is null) return;
        handler.Post(() =>
        {
            session?.Close();
            device?.Close();
            session = null;
            device = null;
            current = (current + 1) % cameras.Length;
            manager!.OpenCamera(cameras[current], new DeviceCallback(this), handler);
            RequestKeyframe();
        });
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
        try
        {
            session?.Close();
            device?.Close();
            encoder?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already stopped
        }
        encoder?.Release();
        input?.Release();
        thread?.QuitSafely();
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Start repeating captures into the encoder's surface.</summary>
    private void OnOpened(CameraDevice opened)
    {
        // A session with the one target
        device = opened;
#pragma warning disable CA1422 // The surface list form is enough here and works on every version
        opened.CreateCaptureSession([input!], new SessionCallback(this), handler);
#pragma warning restore CA1422
    }

    /// <summary>Capture continuously, as for recording.</summary>
    private void OnConfigured(CameraCaptureSession configured)
    {
        // The recording template keeps the frame rate steady
        session = configured;
        CaptureRequest.Builder request = device!.CreateCaptureRequest(CameraTemplate.Record);
        request.AddTarget(input!);
        // Done
        configured.SetRepeatingRequest(request.Build(), null, handler);
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
        public override void OnError(CameraDevice camera, CameraError error) => camera.Close();
    }

    /// <summary>Starts capturing when the session is configured.</summary>
    private sealed class SessionCallback(AndroidCamera owner) : CameraCaptureSession.StateCallback
    {
        /// <inheritdoc />
        public override void OnConfigured(CameraCaptureSession session) => owner.OnConfigured(session);

        /// <inheritdoc />
        public override void OnConfigureFailed(CameraCaptureSession session) => session.Close();
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
