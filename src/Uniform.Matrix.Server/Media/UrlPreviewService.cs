namespace Uniform.Matrix.Server.Media;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1mediapreview_url">URL previews</see>: fetch a page, read its <see href="https://ogp.me/">OpenGraph</see> title, description and image, and store the image in the media repository.</summary>
/// <remarks>Pages and images are fetched through <see cref="Http.PublicAddresses"/>, so previews cannot reach machines inside the server's network unless <see cref="UrlPreviewOptions.AllowPrivateAddresses"/> is on; size and time are limited. Results are kept for <see cref="UrlPreviewOptions.CacheDuration"/> per address.</remarks>
public sealed class UrlPreviewService
{
    /// <summary>Name of the HTTP client that fetches pages.</summary>
    public const string HttpClientName = "Uniform.Matrix.Server.UrlPreviews";
    private const string ImageKey = "og:image";
    private readonly IHttpClientFactory httpClients;
    private readonly MediaService media;
    private readonly IOptionsMonitor<UrlPreviewOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<UrlPreviewService> logger;
    private readonly ConcurrentDictionary<string, (JsonObject Preview, DateTimeOffset Expires)> cache = new();

    /// <summary>Create the service.</summary>
    public UrlPreviewService(IHttpClientFactory httpClients, MediaService media, IOptionsMonitor<UrlPreviewOptions> options, TimeProvider time, ILogger<UrlPreviewService> logger)
    {
        // Store dependencies
        this.httpClients = httpClients;
        this.media = media;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Get whether previews are on, see <see cref="UrlPreviewOptions.Enabled"/>.</summary>
    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>Return the preview of <paramref name="url"/>: <c>og:</c> keys of the page, and the image as an <c>mxc://</c> URI with <c>matrix:image:size</c>; an image link previews as itself.</summary>
    /// <param name="userId">Who asks; owns the stored image.</param>
    /// <param name="url">Absolute http or https address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> GetPreviewAsync(string userId, string url, CancellationToken cancellationToken)
    {
        // Assert previews are on, and a web address
        if (!Enabled) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.Unrecognized, "URL previews are off on this server.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https")) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "url must be an http or https address.");
        string key = address.AbsoluteUri;
        if (cache.TryGetValue(key, out (JsonObject Preview, DateTimeOffset Expires) cached) && cached.Expires > time.GetUtcNow()) return (JsonObject)cached.Preview.DeepClone();

        // Fetch; failures reach the client as one error
        (byte[] body, string contentType, Uri final) = await FetchAsync(address, cancellationToken) ?? throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "Could not fetch the page to preview.");
        JsonObject preview = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? await StoreImageAsync(userId, body, contentType, final, cancellationToken) ?? []
            : await PreviewPageAsync(userId, body, contentType, final, cancellationToken);
        cache[key] = (preview, time.GetUtcNow() + options.CurrentValue.CacheDuration);
        // Return a copy, as the cache keeps the original
        return (JsonObject)preview.DeepClone();
    }

    /// <summary>Return the OpenGraph keys of an HTML page, with title and description from the page itself when it has no OpenGraph ones, and its image stored.</summary>
    private async Task<JsonObject> PreviewPageAsync(string userId, byte[] body, string contentType, Uri page, CancellationToken cancellationToken)
    {
        // Parse without running anything
        string html = Decode(body, contentType);
        IDocument document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        JsonObject preview = [];
        foreach (IElement meta in document.QuerySelectorAll("meta[property^='og:'], meta[name^='og:']"))
        {
            string? name = meta.GetAttribute("property") ?? meta.GetAttribute("name");
            string? value = meta.GetAttribute("content");
            if (name is null || string.IsNullOrWhiteSpace(value) || preview.ContainsKey(name)) continue;
            preview[name] = value.Trim();
        }
        if (!preview.ContainsKey("og:title") && document.Title is { Length: > 0 } title) preview["og:title"] = title.Trim();
        if (!preview.ContainsKey("og:description") && document.QuerySelector("meta[name='description']")?.GetAttribute("content") is { Length: > 0 } description) preview["og:description"] = description.Trim();

        // The image, fetched and stored; without it the preview is text only
        string? image = preview[ImageKey]?.GetValue<string>();
        preview.Remove(ImageKey);
        if (image is not null && Uri.TryCreate(page, image, out Uri? imageAddress) && imageAddress.Scheme is "http" or "https"
            && await FetchAsync(imageAddress, cancellationToken) is (byte[] imageBody, string imageType, Uri imageFinal) && imageType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && await StoreImageAsync(userId, imageBody, imageType, imageFinal, cancellationToken) is JsonObject stored)
        {
            foreach ((string key, JsonNode? value) in stored) preview[key] = value?.DeepClone();
        }
        // Return preview
        return preview;
    }

    /// <summary>Store an image in the media repository; return its preview keys, or null when it cannot be stored.</summary>
    private async Task<JsonObject?> StoreImageAsync(string userId, byte[] body, string contentType, Uri address, CancellationToken cancellationToken)
    {
        // Upload as the user who asked
        try
        {
            string uri = await media.UploadAsync(userId, new MemoryStream(body), contentType, Path.GetFileName(address.AbsolutePath) is { Length: > 0 } name ? name : null, cancellationToken);
            // Return image keys
            return new JsonObject { [ImageKey] = uri, ["og:image:type"] = contentType, ["matrix:image:size"] = body.Length };
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Preview image of {Host} not stored: {Error}", address.Host, ex.Message);
            return null;
        }
    }

    /// <summary>Return the body, content type and final address of a page or image, at most <see cref="UrlPreviewOptions.MaxSize"/> bytes; null when it cannot be fetched.</summary>
    private async Task<(byte[] Body, string ContentType, Uri Final)?> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        // Within the time limit
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.CurrentValue.Timeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,image/*;q=0.9,*/*;q=0.5");
            using HttpResponseMessage response = await httpClients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            // Read up to the limit; a longer page is cut, which still has its head
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] buffer = new byte[options.CurrentValue.MaxSize];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            // Return what was read
            return (buffer[..length], contentType, response.RequestMessage?.RequestUri ?? address);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Preview of {Host} failed: {Error}", address.Host, ex.Message);
            return null;
        }
    }

    /// <summary>Return the text of a page in the charset of its content type, UTF-8 when unknown.</summary>
    private static string Decode(byte[] body, string contentType)
    {
        // Charset from the header
        Encoding encoding = Encoding.UTF8;
        if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? type) && type.CharSet is string charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset: UTF-8
            }
        }
        // Return text
        return encoding.GetString(body);
    }
}
