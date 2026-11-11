namespace Uniform.Chat.Android;

using global::Android.Content;
using global::Android.Runtime;
using global::Android.Hardware.Camera2;
using global::Android.Media;
using global::Android.OS;
using global::Android.Views;
using Uniform.Matrix.Calls;

/// <summary>The front camera with Android's VP8 encoder, for a call's video: Camera2 renders into the encoder's input surface, and each encoded frame comes in <see cref="FrameEncoded"/>.</summary>
/// <remarks>
/// The picture is the sensor's, landscape 640x480 at up to 30 frames per second, or 320x240 at 10 when the peer asks for less, as a slow receiver does; the encoder makes a keyframe every two seconds and when asked.
/// With <see cref="Upright"/>, as through LiveKit, which does not pass the rotation on, Camera2 renders into an image reader instead; each picture is turned upright in C# and written into the encoder's input, and the encoder starts again at the other shape when the phone turns between portrait and landscape.
/// </remarks>
/// <param name="context">The application.</param>
public sealed class AndroidCamera(Context context) : IVideoCamera
{
    private const string Vp8 = "video/x-vnd.on2.vp8";
    /// <summary>Bitrate below which a peer's limit makes the camera fall back to <see cref="Low"/>.</summary>
    private const long FallbackBelow = 300_000;
    /// <summary>Bitrate from which a peer's limit brings the camera back to <see cref="Normal"/>.</summary>
    private const long RestoreFrom = 600_000;
    /// <summary>Shortest time between quality changes, as peers' estimates move about.</summary>
    private static readonly TimeSpan ChangeInterval = TimeSpan.FromSeconds(3);
    private long lastChange;
    private int encoderBitrate;
    private int sensorOrientation;
    private ImageReader? previewReader;
    private long lastPreview;
    private bool paused;
    /// <summary>Size of the preview pictures.</summary>
    private const int PreviewWidth = 176;
    private const int PreviewHeight = 144;
    /// <summary>Shortest time between preview pictures.</summary>
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(100);
    private bool front;
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
    private ImageReader? frameReader;
    private readonly Queue<int> freeInputs = new();
    private int encoderWidth;
    private int encoderHeight;
    private readonly long clockStart = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public bool Upright { get; set; }

    /// <inheritdoc />
    public event Action<byte[]>? FrameEncoded;

    /// <inheritdoc />
    public event Action<Uniform.Matrix.Video.VideoFrame>? Previewed;

    /// <inheritdoc />
    public bool CanSwitch => cameras.Length > 1;

    /// <summary>The rotation that shows the pictures upright: the camera's mounting against how the screen is turned now, the other way round for the front camera, as Android's image orientation is computed.</summary>
    public int Rotation
    {
        get
        {
            // The screen's turn in degrees
#pragma warning disable CA1422 // The default display's rotation is what the camera's pictures turn with
            IWindowManager? windows = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
            int screen = (int)(windows?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0) * 90;
#pragma warning restore CA1422
            // Return rotation
            return front ? (sensorOrientation + screen) % 360 : (sensorOrientation - screen + 360) % 360;
        }
    }

    /// <summary>Start the encoder, then open the front camera into it.</summary>
    public void Start()
    {
        // A thread for the camera's and the encoder's callbacks
        thread = new HandlerThread("camera");
        thread.Start();
        handler = new Handler(thread.Looper!);

        // The encoder, fed by a surface or turned pictures, and the small preview pictures
        StartEncoder();
        StartFrameReader();
        previewReader = ImageReader.NewInstance(PreviewWidth, PreviewHeight, global::Android.Graphics.ImageFormatType.Yuv420888, 2);
        previewReader.SetOnImageAvailableListener(new PreviewListener(this), handler);

        // The front camera, or the first
        CameraManager cameraManager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        manager = cameraManager;
        cameras = cameraManager.GetCameraIdList();
        current = Math.Max(0, Array.FindIndex(cameras, i => cameraManager.GetCameraCharacteristics(i).Get(CameraCharacteristics.LensFacing!) is Java.Lang.Integer facing && facing.IntValue() == (int)LensFacing.Front));
        // Done: the session starts when it opens
        Open();
    }

    /// <summary>Start a new encoder with a new input surface, as a surface that one camera wrote to may not take another on older Android versions; when <see cref="Upright"/>, one fed with pictures in the shape the phone is held in.</summary>
    private void StartEncoder()
    {
        // VP8 at the quality's size, fed by its surface, or at the turned size by pictures
        bool sideways = Upright && Rotation % 180 != 0;
        encoderWidth = sideways ? quality.Height : quality.Width;
        encoderHeight = sideways ? quality.Width : quality.Height;
        MediaFormat format = MediaFormat.CreateVideoFormat(Vp8, encoderWidth, encoderHeight);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)(Upright ? global::Android.Media.MediaCodecCapabilities.Formatyuv420flexible : global::Android.Media.MediaCodecCapabilities.Formatsurface));
        format.SetInteger(MediaFormat.KeyBitRate, quality.Bitrate);
        encoderBitrate = quality.Bitrate;
        format.SetInteger(MediaFormat.KeyFrameRate, quality.FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        encoder = MediaCodec.CreateEncoderByType(Vp8);
        encoder.SetCallback(new EncoderCallback(this), handler);
        encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        if (!Upright) input = encoder.CreateInputSurface();
        // Done
        encoder.Start();
    }

    /// <summary>When <see cref="Upright"/>, make the reader the camera renders the pictures to encode into, at the quality's size; else nothing.</summary>
    private void StartFrameReader()
    {
        // Three pictures in flight, so the camera does not wait
        frameReader?.Close();
        frameReader = null;
        if (!Upright) return;
        frameReader = ImageReader.NewInstance(quality.Width, quality.Height, global::Android.Graphics.ImageFormatType.Yuv420888, 3);
        // Done
        frameReader.SetOnImageAvailableListener(new FrameListener(this), handler);
    }

    /// <summary>The surface the camera renders the pictures to encode into.</summary>
    private Surface EncodingSurface => Upright ? frameReader!.Surface! : input!;

    /// <summary>Note an input buffer of the encoder that is free, for the next turned picture.</summary>
    private void OnInputAvailable(MediaCodec codec, int index)
    {
        // The current encoder's only
        if (!ReferenceEquals(codec, encoder)) return;
        // Done
        lock (freeInputs) freeInputs.Enqueue(index);
    }

    /// <summary>Encode the latest picture turned upright: a new encoder when its shape changed, then the picture into a free input buffer; dropped when none is free.</summary>
    private void OnFrame(ImageReader reader)
    {
        // The latest picture, closed in Java when done
        global::Android.Media.Image? image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null || paused || disposed || encoder is null) return;
            Uniform.Matrix.Video.VideoFrame picture = AndroidImages.ToVideoFrame(image).Rotated(Rotation);
            if (picture.Width != encoderWidth || picture.Height != encoderHeight)
            {
                global::Android.Util.Log.Info("UniformChat", $"Video turns to {picture.Width}x{picture.Height}");
                StopEncoder();
                StartEncoder();
            }

            // A free buffer, filled and queued with the capture time
            int index;
            lock (freeInputs)
            {
                if (!freeInputs.TryDequeue(out index)) return;
            }
            MediaCodec codec = encoder!;
            global::Android.Media.Image? target = codec.GetInputImage(index);
            if (target is null) return;
            AndroidImages.Write(picture, target);
            long microseconds = (long)System.Diagnostics.Stopwatch.GetElapsedTime(clockStart).TotalMicroseconds;
            codec.QueueInputBuffer(index, 0, picture.Width * picture.Height * 3 / 2, microseconds, MediaCodecBufferFlags.None);
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or MediaCodec.CodecException)
        {
            // On an encoder or reader that stopped meanwhile: this picture is dropped
            global::Android.Util.Log.Warn("UniformChat", $"Picture not encoded: {ex.Message}");
        }
        finally
        {
            image?.Close();
        }
    }

    /// <summary>Passes pictures to encode on.</summary>
    private sealed class FrameListener(AndroidCamera owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        /// <inheritdoc />
        public void OnImageAvailable(ImageReader? reader)
        {
            // Done
            if (reader is not null) owner.OnFrame(reader);
        }
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
        lock (freeInputs) freeInputs.Clear();
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
        StartFrameReader();
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
        frameReader?.Close();
        previewReader?.Close();
        thread?.QuitSafely();
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Start repeating captures into the encoder's surface; a camera that fails, as one still closing may, is tried once more.</summary>
    private void OnOpened(CameraDevice opened)
    {
        // The camera's mounting and facing, then a session with the one target
        device = opened;
        CameraCharacteristics characteristics = manager!.GetCameraCharacteristics(opened.Id);
        sensorOrientation = characteristics.Get(CameraCharacteristics.SensorOrientation!) is Java.Lang.Integer mounted ? mounted.IntValue() : 0;
        front = characteristics.Get(CameraCharacteristics.LensFacing!) is Java.Lang.Integer facing && facing.IntValue() == (int)LensFacing.Front;
        if (disposed)
        {
            opened.Close();
            return;
        }
        try
        {
#pragma warning disable CA1422 // The surface list form is enough here and works on every version
            opened.CreateCaptureSession(previewReader is null ? [EncodingSurface] : [EncodingSurface, previewReader.Surface!], new SessionCallback(this), handler);
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
            if (!paused) Capture(configured);
        }
        catch (Exception ex) when (ex is CameraAccessException or Java.Lang.IllegalStateException or Java.Lang.IllegalArgumentException)
        {
            // On a camera that went away
            if (device is CameraDevice failed) Fail(failed, ex);
        }
    }

    /// <summary>Follow the peer's limit: below <see cref="FallbackBelow"/> 320x240 at 10 pictures per second, as a slow receiver asks; from <see cref="RestoreFrom"/> full quality again; between them as is, and at most one change per <see cref="ChangeInterval"/>. A change makes a new encoder at the size, and moves the open camera's session to it.</summary>
    public void LimitBitrate(long bitsPerSecond)
    {
        // The encoder's rate within the quality's, then a quality for the rate, on the camera thread
        SetBitrate(bitsPerSecond);
        Quality? wanted = bitsPerSecond < FallbackBelow ? Low : bitsPerSecond >= RestoreFrom ? Normal : null;
        if (wanted is null || handler is null) return;
        handler.Post(() =>
        {
            if (quality == wanted || disposed || System.Diagnostics.Stopwatch.GetElapsedTime(lastChange) < ChangeInterval) return;
            quality = wanted;
            lastChange = System.Diagnostics.Stopwatch.GetTimestamp();
            global::Android.Util.Log.Info("UniformChat", $"Video changes to {wanted.Width}x{wanted.Height} at {wanted.FrameRate} per second, as the peer asked for {bitsPerSecond} bit/s");
            session?.Close();
            session = null;
            StopEncoder();
            StartEncoder();
            StartFrameReader();
            // Done: the open camera into the new surface
            if (device is CameraDevice open) OnOpened(open);
        });
    }

    /// <summary>Set the encoder's bitrate to <paramref name="bitsPerSecond"/>, at most the quality's, when it changes by a tenth or more.</summary>
    private void SetBitrate(long bitsPerSecond)
    {
        // Changed enough
        int rate = (int)Math.Clamp(bitsPerSecond, 100_000, quality.Bitrate);
        if (Math.Abs(rate - encoderBitrate) < encoderBitrate / 10) return;
        encoderBitrate = rate;
        Bundle parameters = new();
        parameters.PutInt(MediaCodec.ParameterKeyVideoBitrate, rate);
        // Done
        try { encoder?.SetParameters(parameters); }
        catch (Java.Lang.IllegalStateException) { }
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

    /// <summary>Capture continuously into the encoder and the preview, at the quality's frame rate.</summary>
    private void Capture(CameraCaptureSession capturing)
    {
        // The recording template keeps the frame rate steady
        CaptureRequest.Builder request = device!.CreateCaptureRequest(CameraTemplate.Record);
        request.AddTarget(EncodingSurface);
        if (previewReader?.Surface is Surface preview) request.AddTarget(preview);
        if (FrameRateRange() is global::Android.Util.Range range) request.Set(CaptureRequest.ControlAeTargetFpsRange!, range);
        // Done
        capturing.SetRepeatingRequest(request.Build(), null, handler);
    }

    /// <summary>Stop capturing, or start again with a keyframe, on the camera thread.</summary>
    public void SetPaused(bool pause)
    {
        // The session's repeating request
        handler?.Post(() =>
        {
            if (paused == pause || disposed) return;
            paused = pause;
            try
            {
                if (pause) session?.StopRepeating();
                else if (session is CameraCaptureSession capturing)
                {
                    Capture(capturing);
                    RequestKeyframe();
                }
            }
            catch (Exception ex) when (ex is CameraAccessException or Java.Lang.IllegalStateException)
            {
                // Done: a camera that went away comes back on the next switch or call
                global::Android.Util.Log.Warn("UniformChat", $"Camera did not {(pause ? "stop" : "start")}: {ex.Message}");
            }
        });
    }

    /// <summary>Give a preview picture, at most every <see cref="PreviewInterval"/>, as I420 with the camera's rotation.</summary>
    private void OnPreview(ImageReader reader)
    {
        // The latest picture, closed in Java when done, as Dispose only frees the wrapper
        global::Android.Media.Image? image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null || Previewed is null || System.Diagnostics.Stopwatch.GetElapsedTime(lastPreview) < PreviewInterval) return;
            lastPreview = System.Diagnostics.Stopwatch.GetTimestamp();
            Preview(image);
        }
        catch (Java.Lang.IllegalStateException ex)
        {
            // On a reader that is full or closed: no preview this time
            global::Android.Util.Log.Warn("UniformChat", $"Preview skipped: {ex.Message}");
        }
        finally
        {
            image?.Close();
        }
    }

    /// <summary>Give a preview picture as I420 with the camera's rotation.</summary>
    private void Preview(global::Android.Media.Image image)
    {
        // Converted, turned and mirrored as the camera is
        Uniform.Matrix.Video.VideoFrame picture = AndroidImages.ToVideoFrame(image);
        picture.Rotation = Rotation;
        picture.Mirrored = front;
        // Done
        Previewed?.Invoke(picture);
    }

    /// <summary>Passes preview pictures on.</summary>
    private sealed class PreviewListener(AndroidCamera owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        /// <inheritdoc />
        public void OnImageAvailable(ImageReader? reader)
        {
            // Done
            if (reader is not null) owner.OnPreview(reader);
        }
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

    /// <summary>Passes encoded frames on, and free input buffers for turned pictures; with a surface, none come.</summary>
    private sealed class EncoderCallback(AndroidCamera owner) : MediaCodec.Callback
    {
        /// <inheritdoc />
        public override void OnInputBufferAvailable(MediaCodec codec, int index) => owner.OnInputAvailable(codec, index);

        /// <inheritdoc />
        public override void OnOutputBufferAvailable(MediaCodec codec, int index, MediaCodec.BufferInfo info) => owner.OnEncoded(codec, index, info);

        /// <inheritdoc />
        public override void OnError(MediaCodec codec, MediaCodec.CodecException e) { }

        /// <inheritdoc />
        public override void OnOutputFormatChanged(MediaCodec codec, MediaFormat format) { }
    }
}
