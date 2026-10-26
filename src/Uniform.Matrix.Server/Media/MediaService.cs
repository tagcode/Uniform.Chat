namespace Uniform.Matrix.Server.Media;

using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;

/// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#content-repository">media repository</see> of local uploads: files under the media directory, rows in the database.</summary>
/// <remarks>Files are stored at <c>media/ab/cd/abcd…</c> by media ID, thumbnails at <c>media/thumbnails/abcd…/WxH-method</c>.</remarks>
public sealed class MediaService
{
    private const int MediaIdLength = 24;
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly IOptions<ServerOptions> server;
    private readonly IOptionsMonitor<MediaOptions> media;
    private readonly TimeProvider time;
    private readonly string directory;

    /// <summary>A stored file to send: its path, content type and file name.</summary>
    /// <param name="Path">File on disk.</param>
    /// <param name="ContentType">Content type to answer with.</param>
    /// <param name="FileName">File name given at upload, if any.</param>
    public sealed record StoredFile(string Path, string ContentType, string? FileName);

    /// <summary>Create the service over the media directory under the data directory.</summary>
    public MediaService(IDbContextFactory<ServerDbContext> contexts, IOptions<ServerOptions> server, IOptionsMonitor<MediaOptions> media, IOptions<ServerStorageOptions> storage, IHostEnvironment environment, TimeProvider time)
    {
        // Store dependencies
        this.contexts = contexts;
        this.server = server;
        this.media = media;
        this.time = time;
        directory = Path.Combine(storage.Value.ResolveDataDirectory(environment.ApplicationName), storage.Value.MediaDirectoryName);
    }

    /// <summary>Largest upload in bytes.</summary>
    public long MaxUploadSize => media.CurrentValue.MaxUploadSize;

    /// <summary>Store an upload of <paramref name="userId"/>; return its <c>mxc://</c> URI. Throws <c>M_TOO_LARGE</c> past <see cref="MaxUploadSize"/>.</summary>
    public async Task<string> UploadAsync(string userId, Stream content, string contentType, string? fileName, CancellationToken cancellationToken)
    {
        // Copy to a temporary file, counting bytes
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"upload-{Guid.NewGuid():N}.tmp");
        long size;
        try
        {
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                size = await CopyLimitedAsync(content, file, MaxUploadSize, cancellationToken);
            }

            // Move into place under a new ID
            string mediaId = RandomNumberGenerator.GetString(IdAlphabet, MediaIdLength);
            string path = FilePath(mediaId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temporary, path);
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            db.Media.Add(new MediaRow { MediaId = mediaId, UserId = userId, ContentType = contentType, FileName = fileName, Size = size, CreatedTs = time.GetUtcNow().ToUnixTimeMilliseconds() });
            await db.SaveChangesAsync(cancellationToken);
            // Return URI
            return $"mxc://{server.Value.ServerName}/{mediaId}";
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Return a stored file; throw <c>M_NOT_FOUND</c> for unknown media and media of other servers.</summary>
    public async Task<StoredFile> GetAsync(string serverName, string mediaId, CancellationToken cancellationToken)
    {
        // Local media only
        MediaRow row = await FindAsync(serverName, mediaId, cancellationToken);
        // Return file
        return new StoredFile(FilePath(row.MediaId), row.ContentType, row.FileName);
    }

    /// <summary>Return a thumbnail, making it on first request; throw <c>M_UNKNOWN</c> (400) when the media is not an image that can be thumbnailed.</summary>
    public async Task<StoredFile> GetThumbnailAsync(string serverName, string mediaId, int width, int height, string method, CancellationToken cancellationToken)
    {
        // Assert a size and an image
        if (width <= 0 || height <= 0) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "width and height must be positive.");
        MediaRow row = await FindAsync(serverName, mediaId, cancellationToken);
        if (!Thumbnailer.CanThumbnail(row.ContentType)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, $"Cannot make thumbnails of {row.ContentType}.");

        // Made before
        (int w, int h, string m) = Thumbnailer.ChooseSize(width, height, method);
        string path = ThumbnailPath(mediaId, w, h, m);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        ThumbnailRow? made = await db.Thumbnails.AsNoTracking().SingleOrDefaultAsync(t => t.MediaId == mediaId && t.Width == w && t.Height == h && t.Method == m, cancellationToken);
        if (made is not null && File.Exists(path)) return new StoredFile(path, made.ContentType, null);

        // Make and store it
        byte[] image = await File.ReadAllBytesAsync(FilePath(mediaId), cancellationToken);
        (byte[] Data, string ContentType) thumbnail = Thumbnailer.Create(image, w, h, m, media.CurrentValue.MaxThumbnailSourcePixels) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.Unknown, "Cannot make a thumbnail of this image.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, thumbnail.Data, cancellationToken);
        if (made is null) db.Thumbnails.Add(new ThumbnailRow { MediaId = mediaId, Width = w, Height = h, Method = m, ContentType = thumbnail.ContentType, Size = thumbnail.Data.Length });
        await db.SaveChangesAsync(cancellationToken);
        // Return thumbnail
        return new StoredFile(path, thumbnail.ContentType, null);
    }

    /// <summary>Return the row of local media; throw <c>M_NOT_FOUND</c> otherwise.</summary>
    private async Task<MediaRow> FindAsync(string serverName, string mediaId, CancellationToken cancellationToken)
    {
        // IDs as this server makes them, as they become paths; other servers need federation
        if (mediaId.Length != MediaIdLength || !mediaId.All(IdAlphabet.Contains)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
        if (serverName != server.Value.ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media of other servers needs federation, which is not supported yet.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        MediaRow? row = await db.Media.AsNoTracking().SingleOrDefaultAsync(m => m.MediaId == mediaId, cancellationToken);
        // Return row, also when its file went missing
        return row is not null && File.Exists(FilePath(mediaId)) ? row : throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Media not found.");
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
            if (total > limit) throw new MatrixException(HttpStatusCode.RequestEntityTooLarge, "M_TOO_LARGE", $"Uploads are at most {limit} bytes.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>Path of a media file, in two levels of folders by ID.</summary>
    private string FilePath(string mediaId) => Path.Combine(directory, mediaId[..2], mediaId[2..4], mediaId);

    /// <summary>Path of a thumbnail.</summary>
    private string ThumbnailPath(string mediaId, int width, int height, string method) => Path.Combine(directory, "thumbnails", mediaId, $"{width}x{height}-{method}");
}
