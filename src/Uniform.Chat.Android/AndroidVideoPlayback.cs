namespace Uniform.Chat.Android;

using global::Android.Graphics;
using global::Android.Media;
using global::Android.OS;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Video;

/// <summary>Plays a video message with Android's <see cref="MediaPlayer"/>: whatever the phone decodes, such as MP4 with H.264 or H.265 and AAC, or WebM, mostly in hardware; the sound on the media stream, the pictures into an <see cref="ImageReader"/>, converted small for the message.</summary>
/// <remarks>MediaPlayer is made on the calling thread, the UI thread, whose looper gets its events; pictures come on a thread of their own. The file's rotation, e.g. of a video recorded upright, goes with each picture.</remarks>
internal sealed class AndroidVideoPlayback : IMediaPlayback
{
    /// <summary>The widest picture made for a message; wider videos are scaled down while copied.</summary>
    private const int MaxWidth = 640;

    /// <summary>How long opening may take.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    private MediaPlayer? player;
    private ImageReader? reader;
    private HandlerThread? thread;
    private int rotation;

    /// <summary>How long it plays.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>The position played.</summary>
    public TimeSpan Position => player is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(player.CurrentPosition);

    /// <summary>Whether it plays now.</summary>
    public bool IsPlaying => player?.IsPlaying == true;

    /// <summary>Raised with each picture, on the pictures' thread.</summary>
    public event Action<VideoFrame>? PictureReady;

    /// <summary>Raised when it has played to its end.</summary>
    public event Action? Ended;

    /// <summary>Raised when the player fails while playing.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Prepare the player for the file, its pictures into an image reader, and show the first one.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        // The player, its sound as media
        MediaPlayer opened = new();
        player = opened;
        opened.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Movie)!.Build()!);
        opened.SetDataSource(path);
        TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        opened.Prepared += (_, _) => prepared.TrySetResult();
        opened.Error += (_, e) =>
        {
            NotSupportedException failure = new($"Android cannot play it ({e.What}, {e.Extra}).");
            if (!prepared.TrySetException(failure)) Failed?.Invoke(failure);
            e.Handled = true;
        };
        opened.Completion += (_, _) => Ended?.Invoke();
        opened.PrepareAsync();
        await prepared.Task.WaitAsync(OpenTimeout, cancellationToken);

        // Its size and rotation
        if (opened.VideoWidth <= 0 || opened.VideoHeight <= 0) throw new NotSupportedException("The file has no video.");
        Duration = TimeSpan.FromMilliseconds(opened.Duration);
        using (MediaMetadataRetriever metadata = new())
        {
            metadata.SetDataSource(path);
            _ = int.TryParse(metadata.ExtractMetadata(MetadataKey.VideoRotation), out rotation);
        }

        // Pictures into a reader on a thread of their own; the first one shown by moving to the start
        thread = new HandlerThread("VideoPictures");
        thread.Start();
        reader = ImageReader.NewInstance(opened.VideoWidth, opened.VideoHeight, ImageFormatType.Yuv420888, 3);
        reader.SetOnImageAvailableListener(new PictureListener(this), new Handler(thread.Looper!));
        opened.SetSurface(reader.Surface);
        // Done
        await SeekAsync(TimeSpan.Zero);
    }

    /// <summary>Play from the position; from the start when it had ended.</summary>
    public Task PlayAsync()
    {
        // Started
        player?.Start();
        return Task.CompletedTask;
    }

    /// <summary>Pause at the position.</summary>
    public Task PauseAsync()
    {
        // Paused while playing
        if (player?.IsPlaying == true) player.Pause();
        return Task.CompletedTask;
    }

    /// <summary>Move to <paramref name="position"/>, to the picture there where the phone can.</summary>
    public Task SeekAsync(TimeSpan position)
    {
        // Closest picture on Android 8 and later, else the keyframe before
        if (player is null) return Task.CompletedTask;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) player.SeekTo((long)position.TotalMilliseconds, MediaPlayerSeekMode.Closest);
        else player.SeekTo((int)position.TotalMilliseconds);
        // Done
        return Task.CompletedTask;
    }

    /// <summary>Stop, and release the player, the reader and the thread.</summary>
    public ValueTask DisposeAsync()
    {
        // Player first, so no picture comes after the reader closed
        player?.Release();
        player = null;
        reader?.Close();
        reader = null;
        thread?.QuitSafely();
        thread = null;
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Convert the newest picture of the reader, and pass it on.</summary>
    private void OnPicture(ImageReader source)
    {
        // The newest only, closed once copied
        using Image? image = source.AcquireLatestImage();
        if (image is null) return;
        VideoFrame picture = AndroidImages.ToVideoFrame(image, MaxWidth);
        picture.Rotation = rotation;
        image.Close();
        // Done
        PictureReady?.Invoke(picture);
    }

    /// <summary>Calls back on each picture the reader has.</summary>
    private sealed class PictureListener(AndroidVideoPlayback playback) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        /// <summary>Take the newest picture.</summary>
        public void OnImageAvailable(ImageReader? reader)
        {
            // A reader closed meanwhile gives nothing
            if (reader is null) return;
            try { playback.OnPicture(reader); }
            catch (Java.Lang.IllegalStateException) { }
        }
    }
}
