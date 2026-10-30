namespace Uniform.Matrix.Client.Terminal.Ui;

using Uniform.Matrix.Client.Terminal.Ui.Images;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

// Inline ASCII previews of images and the image viewer.
public sealed partial class MainWindow
{
    private const int ThumbnailWidth = 800;
    private const int ThumbnailHeight = 600;
    private const int ViewerMaxDimension = 1600;

    private readonly Dictionary<string, DecodedImage?> thumbnails = [];
    private readonly Dictionary<(string Uri, int Columns, int Rows), IReadOnlyList<string>> renderedPreviews = [];
    private readonly HashSet<string> loadingThumbnails = [];

    /// <summary>Return preview lines of an image entry sized to <paramref name="width"/> columns and the pane's <paramref name="height"/>, starting a background load on first use.</summary>
    private IReadOnlyList<string>? GetPreview(TimelineEntry entry, int width, int height)
    {
        // Assert enabled and an image
        if (!UiOptions.InlineImages || entry.ImageUri is not string uri || width < 8) return null;

        // Not loaded yet
        if (!thumbnails.TryGetValue(uri, out DecodedImage? image))
        {
            LoadThumbnail(entry, uri);
            return ["[loading preview…]"];
        }
        if (image is null) return ["[no preview]"];

        // Size to the pane unless limited by settings
        int columns = UiOptions.ImagePreviewColumns > 0 ? Math.Min(UiOptions.ImagePreviewColumns, width) : width;
        int rows = UiOptions.ImagePreviewRows > 0 ? UiOptions.ImagePreviewRows : Math.Max(4, height * 3 / 4);

        // Render once per size
        if (renderedPreviews.TryGetValue((uri, columns, rows), out IReadOnlyList<string>? lines)) return lines;
        lines = AsciiArt.Render(image, columns, rows, UiOptions.DarkBackground, UiOptions.ImagePreviewStyle);
        renderedPreviews[(uri, columns, rows)] = lines;
        // Return preview
        return lines;
    }

    /// <summary>Download and decode a thumbnail of <paramref name="entry"/> off the UI thread, then redraw the log; use the full image when encrypted or the server has no thumbnail.</summary>
    private void LoadThumbnail(TimelineEntry entry, string uri)
    {
        // Assert first request, connection and content
        if (!loadingThumbnails.Add(uri) || Client is not MatrixClient client) return;
        if (entry.Event?.TryGetContent(out MessageContent? content) != true || content is null) return;

        // Load in the background
        _ = Task.Run(async () =>
        {
            DecodedImage? image = null;
            try
            {
                // The server cannot thumbnail ciphertext, so encrypted images load in full
                StoredMedia? media = content.File is null ? await TryGetThumbnailAsync(client, MxcUri.Parse(uri)) : null;
                media ??= await mediaCache.GetAsync(client, content);
                image = DecodedImage.TryDecode(await File.ReadAllBytesAsync(mediaCache.GetFullPath(media)))?.Downscale(ThumbnailWidth);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Preview of {Uri} failed: {Error}", uri, ex.Message);
            }

            // Show on the UI thread
            app.Invoke(() =>
            {
                thumbnails[uri] = image;
                timeline.Refresh();
            });
        });
    }

    /// <summary>Return the server's thumbnail of <paramref name="uri"/>, or null when it has none.</summary>
    private async Task<StoredMedia?> TryGetThumbnailAsync(MatrixClient client, MxcUri uri)
    {
        // Ask the server
        try
        {
            return await mediaCache.GetThumbnailAsync(client, uri, ThumbnailWidth, ThumbnailHeight);
        }
        catch (MatrixException ex)
        {
            // Done; the caller uses the full image
            logger.LogInformation("No thumbnail for {Uri} ({Error}), using the full image", uri, ex.Message);
            return null;
        }
    }

    /// <summary>Download the full image of <paramref name="content"/> and show it in the viewer.</summary>
    private async Task ViewImageAsync(MessageContent content)
    {
        // Download and decode off the UI thread
        string name = content.Filename ?? content.Body ?? "Image";
        ShowStatus($"Loading {name}…");
        await RunSafelyAsync($"Opening {name}", async client =>
        {
            StoredMedia media = await mediaCache.GetAsync(client, content);
            string path = mediaCache.GetFullPath(media);
            DecodedImage? image = await Task.Run(async () => DecodedImage.TryDecode(await File.ReadAllBytesAsync(path))?.Downscale(ViewerMaxDimension));

            // Show, or explain why not
            app.Invoke(() =>
            {
                RestoreStatus();
                if (image is null) Dialogs.Error(app, name, "This image format cannot be shown. Use Save file to open it in another program.");
                else ClientDialogs.ShowImage(app, name, image);
            });
        });
    }
}
