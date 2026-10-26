namespace Uniform.Matrix.Server.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Media;

/// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#content-repository">content repository</see>: uploads, and the authenticated downloads and thumbnails of <c>/_matrix/client/v1/media</c>.</summary>
/// <remarks>The unauthenticated <c>/_matrix/media/v3</c> downloads are not served, as the spec deprecates them; uploads still use the v3 path.</remarks>
public static class MediaEndpoints
{
    // Files are sandboxed so an uploaded page cannot run scripts on the server's origin
    private const string ContentSecurityPolicy = "sandbox; default-src 'none'; script-src 'none'; plugin-types application/pdf; style-src 'unsafe-inline'; media-src 'self'; object-src 'self';";
    private static readonly string[] InlineTypes = ["text/css", "text/plain", "text/csv", "application/json", "application/ld+json", "image/jpeg", "image/gif", "image/png", "image/apng", "image/webp", "image/avif", "video/mp4", "video/webm", "video/ogg", "video/quicktime", "audio/mp4", "audio/webm", "audio/aac", "audio/mpeg", "audio/ogg", "audio/wave", "audio/wav", "audio/x-wav", "audio/x-pn-wav", "audio/flac", "audio/x-flac"];

    /// <summary>Map the media endpoints.</summary>
    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder routes)
    {
        // Upload and limits
        RouteGroupBuilder upload = routes.MapGroup("/_matrix/media/v3").RequireAccessToken();
        upload.MapPost("/upload", UploadAsync);
        upload.MapGet("/config", (MediaService media) => Config(media));

        // Authenticated reads
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v1/media").RequireAccessToken();
        client.MapGet("/config", (MediaService media) => Config(media));
        client.MapGet("/download/{serverName}/{mediaId}", async (string serverName, string mediaId, HttpContext http, MediaService media, CancellationToken cancellationToken) => Send(http, await media.GetAsync(serverName, mediaId, cancellationToken), null));
        client.MapGet("/download/{serverName}/{mediaId}/{fileName}", async (string serverName, string mediaId, string fileName, HttpContext http, MediaService media, CancellationToken cancellationToken) => Send(http, await media.GetAsync(serverName, mediaId, cancellationToken), fileName));
        client.MapGet("/thumbnail/{serverName}/{mediaId}", async (string serverName, string mediaId, int width, int height, string? method, HttpContext http, MediaService media, CancellationToken cancellationToken) =>
            Send(http, await media.GetThumbnailAsync(serverName, mediaId, width, height, method ?? Thumbnailer.Scale, cancellationToken), null));
        // Return routes
        return routes;
    }

    /// <summary>Store the request body as a new upload; the content type comes from the header, the file name from the query.</summary>
    private static async Task<IResult> UploadAsync(string? filename, HttpContext http, MediaService media, CancellationToken cancellationToken)
    {
        // The repository's limit, not the web server's default
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = media.MaxUploadSize + 1;
        string contentType = http.Request.ContentType ?? "application/octet-stream";
        string uri = await media.UploadAsync(http.GetRequester().UserId, http.Request.Body, contentType, string.IsNullOrEmpty(filename) ? null : filename, cancellationToken);
        // Return URI
        return MatrixHttp.Json(new JsonObject { ["content_uri"] = uri });
    }

    /// <summary>Return the upload limit.</summary>
    private static IResult Config(MediaService media) => MatrixHttp.Json(new JsonObject { ["m.upload.size"] = media.MaxUploadSize });

    /// <summary>Send a stored file, inline only for types browsers show safely.</summary>
    private static IResult Send(HttpContext http, MediaService.StoredFile file, string? fileName)
    {
        // Disposition with the file name
        string type = file.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        ContentDispositionHeaderValue disposition = new(InlineTypes.Contains(type) ? "inline" : "attachment");
        if ((fileName ?? file.FileName) is string name) disposition.SetHttpFileName(name);

        // Sandbox and caching headers
        IHeaderDictionary headers = http.Response.Headers;
        headers.ContentDisposition = disposition.ToString();
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        headers.CacheControl = "private, max-age=86400";
        // Return the file, with range requests for media players
        return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
    }
}
