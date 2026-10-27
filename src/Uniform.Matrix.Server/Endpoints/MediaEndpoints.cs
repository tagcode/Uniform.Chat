namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Media;

/// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#content-repository">content repository</see>: uploads, including media created first and uploaded later, and the authenticated downloads and thumbnails of <c>/_matrix/client/v1/media</c>.</summary>
/// <remarks>The unauthenticated <c>/_matrix/media/v3</c> downloads, which the spec deprecates, answer only while <see cref="Configuration.MediaOptions.AllowUnauthenticatedDownloads"/> is on; uploads still use the v3 path.</remarks>
public static class MediaEndpoints
{
    // Files are sandboxed so an uploaded page cannot run scripts on the server's origin
    private const string ContentSecurityPolicy = "sandbox; default-src 'none'; script-src 'none'; plugin-types application/pdf; style-src 'unsafe-inline'; media-src 'self'; object-src 'self';";
    private static readonly string[] InlineTypes = ["text/css", "text/plain", "text/csv", "application/json", "application/ld+json", "image/jpeg", "image/gif", "image/png", "image/apng", "image/webp", "image/avif", "video/mp4", "video/webm", "video/ogg", "video/quicktime", "audio/mp4", "audio/webm", "audio/aac", "audio/mpeg", "audio/ogg", "audio/wave", "audio/wav", "audio/x-wav", "audio/x-pn-wav", "audio/flac", "audio/x-flac"];

    /// <summary>Map the media endpoints.</summary>
    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder routes)
    {
        // Upload, upload of created media, and limits
        RouteGroupBuilder upload = routes.MapGroup("/_matrix/media/v3").RequireAccessToken();
        upload.MapPost("/upload", UploadAsync).RequireRateLimiting(RateLimits.Writes);
        upload.MapPut("/upload/{serverName}/{mediaId}", UploadPendingAsync).RequireRateLimiting(RateLimits.Writes);
        upload.MapGet("/config", (MediaService media) => Config(media));
        upload.MapGet("/preview_url", PreviewAsync);
        routes.MapGroup("/_matrix/media/v1").RequireAccessToken().MapPost("/create", CreateAsync).RequireRateLimiting(RateLimits.Writes);

        // Authenticated reads
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v1/media").RequireAccessToken();
        client.MapGet("/config", (MediaService media) => Config(media));
        client.MapGet("/preview_url", PreviewAsync);
        MapReads(client, requireSetting: false);

        // The deprecated unauthenticated reads, when the setting allows them
        MapReads(routes.MapGroup("/_matrix/media/v3"), requireSetting: true);

        // Local media for other servers, as multipart: metadata, then the file
        RouteGroupBuilder federation = routes.MapGroup("/_matrix/federation/v1/media").RequireFederation();
        federation.MapGet("/download/{mediaId}", async (string mediaId, long? timeout_ms, MediaService media, CancellationToken cancellationToken) =>
            Multipart(await media.GetLocalAsync(mediaId, Wait(media, false, timeout_ms), cancellationToken)));
        federation.MapGet("/thumbnail/{mediaId}", async (string mediaId, int width, int height, string? method, long? timeout_ms, MediaService media, IOptions<ServerOptions> server, CancellationToken cancellationToken) =>
            Multipart(await media.GetThumbnailAsync(server.Value.ServerName, mediaId, width, height, method ?? Thumbnailer.Scale, Wait(media, false, timeout_ms), cancellationToken)));
        // Return routes
        return routes;
    }

    /// <summary>Map downloads and thumbnails; with <paramref name="requireSetting"/>, they answer only while <see cref="MediaService.AllowUnauthenticatedDownloads"/> is on, checked per request so the setting applies at once.</summary>
    private static void MapReads(RouteGroupBuilder group, bool requireSetting)
    {
        // Downloads, with an optional file name to save as
        group.MapGet("/download/{serverName}/{mediaId}", async (string serverName, string mediaId, long? timeout_ms, HttpContext http, MediaService media, CancellationToken cancellationToken) =>
            Send(http, await media.GetAsync(Source(media, requireSetting, serverName), mediaId, Wait(media, requireSetting, timeout_ms), cancellationToken), null));
        group.MapGet("/download/{serverName}/{mediaId}/{fileName}", async (string serverName, string mediaId, string fileName, long? timeout_ms, HttpContext http, MediaService media, CancellationToken cancellationToken) =>
            Send(http, await media.GetAsync(Source(media, requireSetting, serverName), mediaId, Wait(media, requireSetting, timeout_ms), cancellationToken), fileName));

        // Thumbnails
        group.MapGet("/thumbnail/{serverName}/{mediaId}", async (string serverName, string mediaId, int width, int height, string? method, long? timeout_ms, HttpContext http, MediaService media, CancellationToken cancellationToken) =>
            Send(http, await media.GetThumbnailAsync(Source(media, requireSetting, serverName), mediaId, width, height, method ?? Thumbnailer.Scale, Wait(media, requireSetting, timeout_ms), cancellationToken), null));
    }

    /// <summary>Return <paramref name="serverName"/>; the unauthenticated reads serve only local media, so nobody without an account can make this server fetch from others.</summary>
    private static string Source(MediaService media, bool requireSetting, string serverName) =>
        !requireSetting || media.IsLocal(serverName) ? serverName : throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media of other servers is only served to logged-in users, at /_matrix/client/v1/media.");

    /// <summary>Return how long to wait for content that is not uploaded yet: <c>timeout_ms</c>, 20 seconds by default and at most a minute; throw <c>M_NOT_FOUND</c> for unauthenticated reads while they are off.</summary>
    private static TimeSpan Wait(MediaService media, bool requireSetting, long? timeoutMs)
    {
        // Unauthenticated reads look absent while off, as servers that froze them answer
        if (requireSetting && !media.AllowUnauthenticatedDownloads) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unauthenticated media downloads are off on this server; use /_matrix/client/v1/media.");
        // Return wait
        return TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs ?? 20_000, 0, 60_000));
    }

    /// <summary>Return the preview of the page at <c>url</c>; <c>ts</c>, the time the client would like the preview from, is not used.</summary>
    private static async Task<IResult> PreviewAsync(string? url, HttpContext http, UrlPreviewService previews, CancellationToken cancellationToken) =>
        MatrixHttp.Json(await previews.GetPreviewAsync(http.GetRequester().UserId, url ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "url is missing."), cancellationToken));

    /// <summary>Reserve an <c>mxc://</c> URI whose content is uploaded later.</summary>
    private static async Task<IResult> CreateAsync(HttpContext http, MediaService media, CancellationToken cancellationToken)
    {
        // Pending media of the requester
        (string uri, long expires) = await media.CreatePendingAsync(http.GetRequester().UserId, cancellationToken);
        // Return URI and expiry
        return MatrixHttp.Json(new JsonObject { ["content_uri"] = uri, ["unused_expires_at"] = expires });
    }

    /// <summary>Upload the content of created media; the content type comes from the header, the file name from the query.</summary>
    private static async Task<IResult> UploadPendingAsync(string serverName, string mediaId, string? filename, HttpContext http, MediaService media, CancellationToken cancellationToken)
    {
        // The repository's limit, not the web server's default
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = media.MaxUploadSize + 1;
        string contentType = http.Request.ContentType ?? "application/octet-stream";
        await media.UploadPendingAsync(http.GetRequester().UserId, serverName, mediaId, http.Request.Body, contentType, string.IsNullOrEmpty(filename) ? null : filename, cancellationToken);
        // Done
        return MatrixHttp.Json(new JsonObject());
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

    /// <summary>Return a stored file as the federation media API sends it: <c>multipart/mixed</c> with empty metadata, then the file with its type and name.</summary>
    private static IResult Multipart(MediaService.StoredFile file)
    {
        // A boundary that the file cannot be confused with, as parts are not escaped
        string boundary = "uniform" + Guid.NewGuid().ToString("N");
        ContentDispositionHeaderValue disposition = new("attachment");
        if (file.FileName is string name) disposition.SetHttpFileName(name);
        // Return the parts as they stream
        return Results.Stream(async body =>
        {
            byte[] head = System.Text.Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: application/json\r\n\r\n{{}}\r\n--{boundary}\r\nContent-Type: {file.ContentType}\r\nContent-Disposition: {disposition}\r\n\r\n");
            await body.WriteAsync(head);
            await using (FileStream content = File.OpenRead(file.Path)) await content.CopyToAsync(body);
            await body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));
        }, $"multipart/mixed; boundary={boundary}");
    }

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
