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

/// <summary>Shows the newest picture of a <see cref="VideoFeed"/>, scaled to fit with its aspect ratio kept; a new picture is drawn at most once per rendered frame, and pictures that come meanwhile are skipped.</summary>
public sealed class VideoView : Control
{
    /// <summary>The video shown.</summary>
    public static readonly StyledProperty<VideoFeed?> FeedProperty = AvaloniaProperty.Register<VideoView, VideoFeed?>(nameof(Feed));
    private WriteableBitmap? bitmap;
    private byte[] pixels = [];
    private int posted;

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
        if (change.NewValue is VideoFeed feed && VisualRoot is not null) feed.Updated += OnUpdated;
        // Done
        Show();
    }

    /// <summary>Follow the feed while shown.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Subscribe, and show what is there
        base.OnAttachedToVisualTree(e);
        if (Feed is VideoFeed feed) feed.Updated += OnUpdated;
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

    /// <summary>Return the size that fits the picture's aspect ratio in <paramref name="availableSize"/>; nothing without a picture.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // The picture's own size where unconstrained
        if (bitmap is null) return default;
        double aspect = bitmap.Size.Height / bitmap.Size.Width;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : bitmap.Size.Width;
        double height = width * aspect;
        if (double.IsFinite(availableSize.Height) && height > availableSize.Height) (width, height) = (availableSize.Height / aspect, availableSize.Height);
        // Return size
        return new Size(width, height);
    }

    /// <summary>Draw the picture centered and scaled to fit.</summary>
    public override void Render(DrawingContext context)
    {
        // Fit inside the bounds
        if (bitmap is null) return;
        double scale = Math.Min(Bounds.Width / bitmap.Size.Width, Bounds.Height / bitmap.Size.Height);
        Size size = bitmap.Size * scale;
        Rect target = new(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        // Done
        context.DrawImage(bitmap, new Rect(bitmap.Size), target);
    }

    /// <summary>Schedule drawing the newest picture, once until it is drawn.</summary>
    private void OnUpdated()
    {
        // One post at a time
        if (Interlocked.Exchange(ref posted, 1) == 1) return;
        // Done
        Dispatcher.UIThread.Post(Show, DispatcherPriority.Render);
    }

    /// <summary>Copy the newest picture into the bitmap, sized to it, and redraw.</summary>
    private void Show()
    {
        // The newest, or none
        Interlocked.Exchange(ref posted, 0);
        if (Feed?.Latest is not VideoFrame picture)
        {
            bitmap?.Dispose();
            bitmap = null;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        // A bitmap of the picture's size
        if (bitmap is null || bitmap.PixelSize.Width != picture.Width || bitmap.PixelSize.Height != picture.Height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(picture.Width, picture.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            InvalidateMeasure();
        }

        // Converted, then copied into the bitmap's memory
        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            int length = frame.RowBytes * picture.Height;
            if (pixels.Length != length) pixels = new byte[length];
            picture.CopyToBgra(pixels, frame.RowBytes);
            Marshal.Copy(pixels, 0, frame.Address, length);
        }
        // Done
        InvalidateVisual();
    }
}
