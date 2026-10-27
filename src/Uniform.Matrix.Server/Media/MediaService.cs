namespace Uniform.Matrix.Server.Media;

using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Storage;

/// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#content-repository">media repository</see>: local uploads, and media of other servers fetched once through <see href="https://spec.matrix.org/latest/server-server-api/#content-repository">federation</see>; files under the media directory, rows in the database.</summary>
/// <remarks>Files are stored at <c>media/ab/cd/abcd…</c> by media ID, thumbnails at <c>media/thumbnails/abcd…/WxH-method</c>; media of other servers under an ID of this server, see <see cref="RemoteMediaRow"/>. Thumbnails of remote media are made here from the whole file.</remarks>
public sealed class MediaService
{
    private const int MediaIdLength = 24;
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly IOptions<ServerOptions> server;
    private readonly IOptionsMonitor<MediaOptions> media;
    private readonly TimeProvider time;
    private readonly FederationClient federation;
    private readonly ILogger<MediaService> logger;
    private readonly string directory;

    /// <summary>Media found here: the ID of its file, its content type and file name.</summary>
    private sealed record Found(string LocalId, string ContentType, string? FileName);

    /// <summary>A stored file to send: its path, content type and file name.</summary>
    /// <param name="Path">File on disk.</param>
    /// <param name="ContentType">Content type to answer with.</param>
    /// <param name="FileName">File name given at upload, if any.</param>
    public sealed record StoredFile(string Path, string ContentType, string? FileName);

    /// <summary>Create the service over the media directory under the data directory.</summary>
    public MediaService(IDbContextFactory<ServerDbContext> contexts, IOptions<ServerOptions> server, IOptionsMonitor<MediaOptions> media, IOptions<ServerStorageOptions> storage, IHostEnvironment environment, TimeProvider time, FederationClient federation, ILogger<MediaService> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.server = server;
        this.media = media;
        this.time = time;
        this.federation = federation;
        this.logger = logger;
        directory = Path.Combine(storage.Value.ResolveDataDirectory(environment.ApplicationName), storage.Value.MediaDirectoryName);
    }

    /// <summary>Return whether <paramref name="serverName"/> is this server, whose media is local.</summary>
    public bool IsLocal(string serverName) => serverName == server.Value.ServerName;

    /// <summary>Largest upload in bytes.</summary>
    public long MaxUploadSize => media.CurrentValue.MaxUploadSize;

    /// <summary>Whether the deprecated unauthenticated downloads are served, see <see cref="MediaOptions.AllowUnauthenticatedDownloads"/>.</summary>
    public bool AllowUnauthenticatedDownloads => media.CurrentValue.AllowUnauthenticatedDownloads;

    /// <summary>Store an upload of <paramref name="userId"/>; return its <c>mxc://</c> URI. Throws <c>M_TOO_LARGE</c> past <see cref="MaxUploadSize"/>.</summary>
    public async Task<string> UploadAsync(string userId, Stream content, string contentType, string? fileName, CancellationToken cancellationToken)
    {
        // A new ID, then the file and its row
        string mediaId = RandomNumberGenerator.GetString(IdAlphabet, MediaIdLength);
        long size = await StoreFileAsync(mediaId, content, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Media.Add(new MediaRow { MediaId = mediaId, UserId = userId, ContentType = contentType, FileName = fileName, Size = size, CreatedTs = time.GetUtcNow().ToUnixTimeMilliseconds() });
        await db.SaveChangesAsync(cancellationToken);
        // Return URI
        return $"mxc://{server.Value.ServerName}/{mediaId}";
    }

    /// <summary>Reserve an <c>mxc://</c> URI for content <paramref name="userId"/> uploads later with <see cref="UploadPendingAsync"/>; return it and when it expires, in milliseconds since 1970. Throws <c>M_LIMIT_EXCEEDED</c> past <see cref="MediaOptions.MaxPendingUploads"/>.</summary>
    /// <remarks>A pending row has an empty content type until its content arrives.</remarks>
    public async Task<(string ContentUri, long ExpiresAt)> CreatePendingAsync(string userId, CancellationToken cancellationToken)
    {
        // Assert room for another pending upload
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        long lifetime = (long)media.CurrentValue.PendingUploadLifetime.TotalMilliseconds;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        int pending = await db.Media.CountAsync(m => m.UserId == userId && m.ContentType == "" && m.CreatedTs > now - lifetime, cancellationToken);
        if (pending >= media.CurrentValue.MaxPendingUploads) throw new MatrixException(HttpStatusCode.TooManyRequests, ErrorCodes.LimitExceeded, "Too many media are waiting for their content; upload them first.");

        // Row without content
        string mediaId = RandomNumberGenerator.GetString(IdAlphabet, MediaIdLength);
        db.Media.Add(new MediaRow { MediaId = mediaId, UserId = userId, ContentType = "", CreatedTs = now });
        await db.SaveChangesAsync(cancellationToken);
        // Return URI and expiry
        return ($"mxc://{server.Value.ServerName}/{mediaId}", now + lifetime);
    }

    /// <summary>Upload the content of media created with <see cref="CreatePendingAsync"/>: only by its creator, once, and before it expires.</summary>
    public async Task UploadPendingAsync(string userId, string serverName, string mediaId, Stream content, string contentType, string? fileName, CancellationToken cancellationToken)
    {
        // Assert own pending media that has not expired
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        MediaRow row = await FindRowAsync(db, serverName, mediaId, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
        if (row.UserId != userId) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Only the creator of the media may upload its content.");
        if (row.ContentType != "") throw new MatrixException(HttpStatusCode.Conflict, ErrorCodes.CannotOverwriteMedia, "The media already has content.");
        if (row.CreatedTs + (long)media.CurrentValue.PendingUploadLifetime.TotalMilliseconds < time.GetUtcNow().ToUnixTimeMilliseconds()) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "The media expired before its content was uploaded.");

        // File first, then mark the row uploaded
        row.Size = await StoreFileAsync(mediaId, content, cancellationToken);
        row.ContentType = contentType;
        row.FileName = fileName;
        // Done
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Copy <paramref name="content"/> to the file of <paramref name="mediaId"/> through a temporary file; return its size. Throws <c>M_TOO_LARGE</c> past <see cref="MaxUploadSize"/>.</summary>
    private async Task<long> StoreFileAsync(string mediaId, Stream content, CancellationToken cancellationToken)
    {
        // Copy to a temporary file, counting bytes
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            long size;
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                size = await CopyLimitedAsync(content, file, MaxUploadSize, cancellationToken);
            }

            // Move into place
            string path = FilePath(mediaId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temporary, path);
            // Return size
            return size;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Return a stored file, fetching media of other servers first; throw <c>M_NOT_FOUND</c> for unknown media. Local media whose content is not uploaded yet is waited for up to <paramref name="timeout"/>, then throws <c>M_NOT_YET_UPLOADED</c>.</summary>
    public async Task<StoredFile> GetAsync(string serverName, string mediaId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Local or fetched
        Found found = await FindAsync(serverName, mediaId, timeout, cancellationToken);
        // Return file
        return new StoredFile(FilePath(found.LocalId), found.ContentType, found.FileName);
    }

    /// <summary>Return a local file for another server, as <c>/_matrix/federation/v1/media/download</c> serves it; throw <c>M_NOT_FOUND</c> for unknown media. Media of other servers is not passed on.</summary>
    public Task<StoredFile> GetLocalAsync(string mediaId, TimeSpan timeout, CancellationToken cancellationToken) => GetAsync(server.Value.ServerName, mediaId, timeout, cancellationToken);

    /// <summary>Return a thumbnail, making it on first request; throw <c>M_UNKNOWN</c> (400) when the media is not an image that can be thumbnailed.</summary>
    public async Task<StoredFile> GetThumbnailAsync(string serverName, string mediaId, int width, int height, string method, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Assert a size and an image
        if (width <= 0 || height <= 0) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "width and height must be positive.");
        Found found = await FindAsync(serverName, mediaId, timeout, cancellationToken);
        if (!Thumbnailer.CanThumbnail(found.ContentType)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, $"Cannot make thumbnails of {found.ContentType}.");

        // Made before
        string localId = found.LocalId;
        (int w, int h, string m) = Thumbnailer.ChooseSize(width, height, method);
        string path = ThumbnailPath(localId, w, h, m);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        ThumbnailRow? made = await db.Thumbnails.AsNoTracking().SingleOrDefaultAsync(t => t.MediaId == localId && t.Width == w && t.Height == h && t.Method == m, cancellationToken);
        if (made is not null && File.Exists(path)) return new StoredFile(path, made.ContentType, null);

        // Make and store it
        byte[] image = await File.ReadAllBytesAsync(FilePath(localId), cancellationToken);
        (byte[] Data, string ContentType) thumbnail = Thumbnailer.Create(image, w, h, m, media.CurrentValue.MaxThumbnailSourcePixels) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, "Cannot make a thumbnail of this image.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, thumbnail.Data, cancellationToken);
        if (made is null) db.Thumbnails.Add(new ThumbnailRow { MediaId = localId, Width = w, Height = h, Method = m, ContentType = thumbnail.ContentType, Size = thumbnail.Data.Length });
        await db.SaveChangesAsync(cancellationToken);
        // Return thumbnail
        return new StoredFile(path, thumbnail.ContentType, null);
    }

    /// <summary>Return local media with content, waiting up to <paramref name="timeout"/> for pending content, or media of another server, fetched on first use; throw <c>M_NOT_FOUND</c> or <c>M_NOT_YET_UPLOADED</c> otherwise.</summary>
    private async Task<Found> FindAsync(string serverName, string mediaId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Other servers' media is kept here once fetched
        if (serverName != server.Value.ServerName) return await FindRemoteAsync(serverName, mediaId, cancellationToken);

        // Poll while the content is pending; uploads are rare enough not to need a signal
        DateTimeOffset end = time.GetUtcNow() + timeout;
        while (true)
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            MediaRow? row = await FindRowAsync(db, serverName, mediaId, cancellationToken);
            if (row is null) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
            if (row.ContentType != "") return File.Exists(FilePath(mediaId)) ? new Found(row.MediaId, row.ContentType, row.FileName) : throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
            if (time.GetUtcNow() >= end) throw new MatrixException(HttpStatusCode.GatewayTimeout, ErrorCodes.NotYetUploaded, "The media's content has not been uploaded yet.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken);
        }
    }

    /// <summary>Return the row of local media, pending or not, or null; throw <c>M_NOT_FOUND</c> for IDs this server does not make and for other servers.</summary>
    private async Task<MediaRow?> FindRowAsync(ServerDbContext db, string serverName, string mediaId, CancellationToken cancellationToken)
    {
        // IDs as this server makes them, as they become paths; other servers need federation
        if (mediaId.Length != MediaIdLength || !mediaId.All(IdAlphabet.Contains)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
        if (serverName != server.Value.ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media of other servers needs federation, which is not supported yet.");
        // Return row
        return await db.Media.SingleOrDefaultAsync(m => m.MediaId == mediaId, cancellationToken);
    }

    /// <summary>Return media of another server, fetching it on first use: through the authenticated federation media API, else its legacy download.</summary>
    private async Task<Found> FindRemoteAsync(string serverName, string mediaId, CancellationToken cancellationToken)
    {
        // Kept before
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RemoteMediaRow? kept = await db.RemoteMedia.AsNoTracking().SingleOrDefaultAsync(m => m.ServerName == serverName && m.MediaId == mediaId, cancellationToken);
        if (kept is not null && File.Exists(FilePath(kept.LocalId))) return new Found(kept.LocalId, kept.ContentType, kept.FileName);
        if (mediaId.Length == 0 || mediaId.Length > 255 || mediaId.Any(c => c is '/' or '\\' or '?' or '#')) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");

        // Fetch into a file of our own ID
        string localId = kept?.LocalId ?? RandomNumberGenerator.GetString(IdAlphabet, MediaIdLength);
        (long size, string contentType, string? fileName) fetched;
        try
        {
            fetched = await federation.DownloadAsync(serverName, $"/_matrix/federation/v1/media/download/{Uri.EscapeDataString(mediaId)}", (response, token) => ReadMultipartAsync(localId, response, token), cancellationToken);
        }
        catch (MatrixException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.MethodNotAllowed && ex.Errcode is ErrorCodes.Unrecognized or ErrorCodes.Unknown or ErrorCodes.Unauthorized)
        {
            // Servers without authenticated media; unsigned, as they were
            logger.LogDebug("{Server} has no authenticated media download, using the legacy one: {Error}", serverName, ex.Message);
            fetched = await federation.DownloadAsync(serverName, $"/_matrix/media/v3/download/{Uri.EscapeDataString(serverName)}/{Uri.EscapeDataString(mediaId)}?allow_remote=false", (response, token) => ReadPlainAsync(localId, response, token), cancellationToken, signed: false);
        }
        catch (MatrixException ex) when (ex.StatusCode == HttpStatusCode.Forbidden && ex.Errcode == ErrorCodes.Forbidden)
        {
            throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media of other servers needs federation, which is off on this server.");
        }

        // Row last; another request that fetched it meanwhile wins
        if (kept is null)
        {
            db.RemoteMedia.Add(new RemoteMediaRow { ServerName = serverName, MediaId = mediaId, LocalId = localId, ContentType = fetched.contentType, FileName = fetched.fileName, Size = fetched.size, FetchedTs = time.GetUtcNow().ToUnixTimeMilliseconds() });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return await FindRemoteAsync(serverName, mediaId, cancellationToken);
            }
        }
        // Return fetched media
        return new Found(localId, fetched.contentType, fetched.fileName);
    }

    /// <summary>Store the file of a <c>multipart/mixed</c> media answer: metadata first, then the content, whose own headers give its type and name.</summary>
    private async Task<(long Size, string ContentType, string? FileName)> ReadMultipartAsync(string localId, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Assert multipart with a boundary
        if (response.Content.Headers.ContentType is not { MediaType: "multipart/mixed" } type || type.Parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"') is not { Length: > 0 } boundary) throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "The media answer is not multipart.");
        MultipartReader reader = new(boundary, await response.Content.ReadAsStreamAsync(cancellationToken));

        // Metadata, then the content; a redirect to elsewhere is not followed
        _ = await reader.ReadNextSectionAsync(cancellationToken) ?? throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "The media answer has no metadata.");
        MultipartSection content = await reader.ReadNextSectionAsync(cancellationToken) ?? throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "The media answer has no content.");
        if (content.Headers?.ContainsKey(HeaderNames.Location) == true) throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, "The media is elsewhere, which is not followed.");
        long size = await StoreFileAsync(localId, content.Body, cancellationToken);
        string? fileName = content.ContentDisposition is string disposition && Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(disposition, out Microsoft.Net.Http.Headers.ContentDispositionHeaderValue? parsed) ? (parsed.FileNameStar.Value ?? parsed.FileName.Value)?.Trim('"') : null;
        // Return stored file
        return (size, content.ContentType ?? "application/octet-stream", fileName);
    }

    /// <summary>Store the file of a plain media answer, as the legacy download gives it.</summary>
    private async Task<(long Size, string ContentType, string? FileName)> ReadPlainAsync(string localId, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Body as the file, headers for type and name
        long size = await StoreFileAsync(localId, await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
        System.Net.Http.Headers.ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        // Return stored file
        return (size, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"'));
    }

    /// <summary>Copy up to <paramref name="limit"/> bytes; throw <c>M_TOO_LARGE</c> when there are more.</summary>
    private static async Task<long> CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        // Copy in blocks, checking the total
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return total;
            total += read;
            if (total > limit) throw new MatrixException(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.TooLarge, $"Uploads are at most {limit} bytes.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>Path of a media file, in two levels of folders by ID.</summary>
    private string FilePath(string mediaId) => Path.Combine(directory, mediaId[..2], mediaId[2..4], mediaId);

    /// <summary>Path of a thumbnail.</summary>
    private string ThumbnailPath(string mediaId, int width, int height, string method) => Path.Combine(directory, "thumbnails", mediaId, $"{width}x{height}-{method}");
}
