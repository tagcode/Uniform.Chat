namespace Uniform.Chat.Avalonia.Views;

using System.Runtime.InteropServices;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using global::Avalonia.Threading;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Video;

/// <summary>Shows the newest picture of a <see cref="VideoFeed"/>, turned upright by its rotation and scaled to fit with its aspect ratio kept; a new picture is drawn at most once per rendered frame, and pictures that come meanwhile are skipped.</summary>
public sealed class VideoView : Control
{
    /// <summary>The video shown.</summary>
    public static readonly StyledProperty<VideoFeed?> FeedProperty = AvaloniaProperty.Register<VideoView, VideoFeed?>(nameof(Feed));
    private WriteableBitmap? bitmap;
    private byte[] pixels = [];
    private int posted;
    private int wanted;
    private volatile VideoFeed? feed;
    private int rotation;
    private bool mirrored;

    /// <summary>Whether to show the pictures mirrored, besides those that say so themselves.</summary>
    public static readonly StyledProperty<bool> MirrorProperty = AvaloniaProperty.Register<VideoView, bool>(nameof(Mirror));

    /// <summary>Whether to show the picture mirrored, as a preview of one's own front camera is.</summary>
    public bool Mirror
    {
        get => GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    /// <summary>The video shown.</summary>
    public VideoFeed? Feed
    {
        get => GetValue(FeedProperty);
        set => SetValue(FeedProperty, value);
    }

    /// <summary>Follow a new feed.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        // Feed changes only
        base.OnPropertyChanged(change);
        if (change.Property != FeedProperty) return;
        if (change.OldValue is VideoFeed old) old.Updated -= OnUpdated;
        feed = change.NewValue as VideoFeed;
        if (feed is not null && VisualRoot is not null) feed.Updated += OnUpdated;
        // Done
        Show();
    }

    /// <summary>Follow the feed while shown.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Subscribe, and show what is there
        base.OnAttachedToVisualTree(e);
        feed = Feed;
        if (feed is not null) feed.Updated += OnUpdated;
        // Done
        Show();
    }

    /// <summary>Stop following the feed, and release the bitmap.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Unsubscribe
        base.OnDetachedFromVisualTree(e);
        if (Feed is VideoFeed feed) feed.Updated -= OnUpdated;
        bitmap?.Dispose();
        // Done
        bitmap = null;
    }

    /// <summary>Whether the picture is turned sideways, so its width shows as height.</summary>
    private bool Turned => rotation is 90 or 270;

    /// <summary>Return the size that fits the picture's aspect ratio in <paramref name="availableSize"/>; nothing without a picture. Before its bitmap is made, e.g. when a list shows the view again, the feed's newest picture gives the shape, so the view does not grow later and move what is around it.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // The bitmap's shape, else the newest picture's
        VideoFrame? latest = feed?.Latest;
        if (bitmap is null && latest is null) return default;
        bool turned = bitmap is null ? latest!.Rotation is 90 or 270 : Turned;
        Size shape = bitmap?.Size ?? new Size(latest!.Width, latest.Height);

        // The picture's own size where unconstrained
        double aspect = turned ? shape.Width / shape.Height : shape.Height / shape.Width;
        Size own = turned ? new Size(shape.Height, shape.Width) : shape;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : own.Width;
        double height = width * aspect;
        if (double.IsFinite(availableSize.Height) && height > availableSize.Height) (width, height) = (availableSize.Height / aspect, availableSize.Height);
        // Return size
        return new Size(width, height);
    }

    /// <summary>Draw the picture centered and scaled to fit.</summary>
    public override void Render(DrawingContext context)
    {
        // Fit inside the bounds, as turned
        if (bitmap is null) return;
        Size shown = Turned ? new Size(bitmap.Size.Height, bitmap.Size.Width) : bitmap.Size;
        double scale = Math.Min(Bounds.Width / shown.Width, Bounds.Height / shown.Height);
        Size size = bitmap.Size * scale;

        // Drawn around the center, turned clockwise
        Matrix turn = Matrix.CreateTranslation(-size.Width / 2, -size.Height / 2) * Matrix.CreateRotation(rotation * Math.PI / 180) * Matrix.CreateScale(Mirror != mirrored ? -1 : 1, 1) * Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);
        using (context.PushTransform(turn))
        {
            // Done
            context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(size));
        }
    }

    /// <summary>On any thread, e.g. the decoder's: convert the newest picture to screen colors on the thread pool, reading the feed from a field, as Avalonia properties are for the UI thread only; then show it on the UI thread, which only copies it, so that input is not held up on slow devices; one picture at a time, into the one buffer, and of those that come meanwhile only the newest, after it.</summary>
    private void OnUpdated()
    {
        // Wanted first, so one in flight takes it when done; else this one goes
        Interlocked.Exchange(ref wanted, 1);
        if (Interlocked.Exchange(ref posted, 1) == 1) return;
        Interlocked.Exchange(ref wanted, 0);
        VideoFrame? picture = feed?.Latest;
        if (picture is null)
        {
            Dispatcher.UIThread.Post(Clear);
            return;
        }
        // Done: converted off the UI thread, into the buffer no one else uses meanwhile
        _ = Task.Run(() =>
        {
            try
            {
                int length = picture.Width * 4 * picture.Height;
                if (pixels.Length != length) pixels = new byte[length];
                picture.CopyToBgra(pixels, picture.Width * 4);
                Dispatcher.UIThread.Post(() => Show(picture.Width, picture.Height, picture.Rotation, picture.Mirrored));
            }
            catch
            {
                // On a failed conversion: ready for the next picture
                Next();
                throw;
            }
        });
    }

    /// <summary>Show the newest picture, e.g. when the feed changes: now, or after the one in flight; measured by it meanwhile.</summary>
    private void Show()
    {
        // Through the same path, never two at once into the one buffer
        InvalidateMeasure();
        // Done
        OnUpdated();
    }

    /// <summary>Let the next picture be converted, and convert the newest if one came meanwhile.</summary>
    private void Next()
    {
        // Free, then what was wanted
        Interlocked.Exchange(ref posted, 0);
        // Done
        if (Interlocked.CompareExchange(ref wanted, 0, 0) == 1) OnUpdated();
    }

    /// <summary>Show no picture.</summary>
    private void Clear()
    {
        // Gone, and ready for the next
        bitmap?.Dispose();
        bitmap = null;
        InvalidateMeasure();
        InvalidateVisual();
        // Done
        Next();
    }

    /// <summary>Copy the converted pixels of a <paramref name="width"/> by <paramref name="height"/> picture into the bitmap, sized to it, and redraw; then take the next picture.</summary>
    private void Show(int width, int height, int pictureRotation, bool pictureMirrored)
    {
        // Mirrored as the picture says
        mirrored = pictureMirrored;

        // Turned sideways changes the shape
        if (pictureRotation != rotation)
        {
            rotation = pictureRotation;
            InvalidateMeasure();
        }

        // A bitmap of the picture's size
        if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            InvalidateMeasure();
        }

        // Row by row when the bitmap's rows are padded
        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            int row = width * 4;
            if (frame.RowBytes == row) Marshal.Copy(pixels, 0, frame.Address, row * height);
            else for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * row, frame.Address + y * frame.RowBytes, row);
        }
        InvalidateVisual();
        // Done: the next picture may come
        Next();
    }
}
