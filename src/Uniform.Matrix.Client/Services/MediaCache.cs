namespace Uniform.Matrix.Client.Services;

using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>Downloads media and thumbnails once and serves them from the account's media directory afterwards.</summary>
public sealed class MediaCache
{
    private readonly ClientStore store;
    private readonly AccountPaths paths;
    private readonly TimeProvider time;

    /// <summary>Create a cache storing files under <see cref="AccountPaths.MediaDirectory"/>.</summary>
    public MediaCache(ClientStore store, AccountPaths paths, TimeProvider time)
    {
        // Store dependencies
        this.store = store;
        this.paths = paths;
        this.time = time;
    }

    /// <summary>Return the full file of <paramref name="uri"/>, downloading it when not cached or the file is gone.</summary>
    public Task<StoredMedia> GetAsync(MatrixClient client, MxcUri uri, CancellationToken cancellationToken = default) =>
        GetOrDownloadAsync(uri.ToString(), Path.Combine(AccountPaths.ToSafeName(uri.ServerName), AccountPaths.ToSafeName(uri.MediaId)), async file =>
        {
            using MediaDownload media = await client.DownloadMediaAsync(uri, cancellationToken);
            await media.Content.CopyToAsync(file, cancellationToken);
            return (media.ContentType, media.FileName);
        }, cancellationToken);

    /// <summary>Return the attachment of <paramref name="content"/>, decrypting <see cref="MessageContent.File"/> of encrypted rooms.</summary>
    public Task<StoredMedia> GetAsync(MatrixClient client, MessageContent content, CancellationToken cancellationToken = default)
    {
        // Unencrypted
        if (content.File is not EncryptedFile encrypted) return GetAsync(client, MxcUri.Parse(content.Url!), cancellationToken);

        // Encrypted; stored decrypted after the hash check, kept apart from plain media of the same URI
        MxcUri uri = MxcUri.Parse(encrypted.Url!);
        string relativePath = Path.Combine("encrypted", AccountPaths.ToSafeName(uri.ServerName), AccountPaths.ToSafeName(uri.MediaId));
        // Return decrypted file
        return GetOrDownloadAsync($"{uri}#encrypted", relativePath, async file =>
        {
            byte[] plaintext = await client.DownloadEncryptedAsync(encrypted, cancellationToken);
            await file.WriteAsync(plaintext, cancellationToken);
            return (content.Info?.Mimetype, content.Filename ?? content.Body);
        }, cancellationToken);
    }

    /// <summary>Return a thumbnail of <paramref name="uri"/> of about <paramref name="width"/>×<paramref name="height"/> pixels, downloading it when not cached.</summary>
    public Task<StoredMedia> GetThumbnailAsync(MatrixClient client, MxcUri uri, int width, int height, CancellationToken cancellationToken = default) =>
        GetOrDownloadAsync($"{uri}#thumbnail={width}x{height}", Path.Combine("thumbnails", AccountPaths.ToSafeName(uri.ServerName), $"{AccountPaths.ToSafeName(uri.MediaId)}_{width}x{height}"), async file =>
        {
            using MediaDownload media = await client.DownloadThumbnailAsync(uri, width, height, cancellationToken: cancellationToken);
            await media.Content.CopyToAsync(file, cancellationToken);
            return (media.ContentType, media.FileName);
        }, cancellationToken);

    /// <summary>Return the absolute path of <paramref name="media"/>.</summary>
    public string GetFullPath(StoredMedia media) => Path.Combine(paths.MediaDirectory, media.RelativePath);

    /// <summary>Return the cached file stored under <paramref name="key"/>, or download it to <paramref name="relativePath"/>.</summary>
    /// <param name="key">Database key; the content URI, with a suffix for thumbnails.</param>
    /// <param name="relativePath">File path under the media directory.</param>
    /// <param name="download">Writes the file and returns its MIME type and name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<StoredMedia> GetOrDownloadAsync(string key, string relativePath, Func<FileStream, Task<(string? ContentType, string? FileName)>> download, CancellationToken cancellationToken)
    {
        // Cached file
        StoredMedia? cached = await store.FindMediaAsync(key, cancellationToken);
        if (cached is not null && File.Exists(GetFullPath(cached))) return cached;

        // Download to a temporary file, then move, so a partial download is never served
        string fullPath = Path.Combine(paths.MediaDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string tempPath = fullPath + ".part";
        (string? contentType, string? fileName) = (null, null);
        await using (FileStream file = File.Create(tempPath)) (contentType, fileName) = await download(file);
        File.Move(tempPath, fullPath, overwrite: true);

        // Record metadata
        StoredMedia stored = new() { MxcUri = key, RelativePath = relativePath, ContentType = contentType, FileName = fileName, Size = new FileInfo(fullPath).Length, DownloadedAt = time.GetUtcNow() };
        await store.SaveMediaAsync(stored, cancellationToken);
        // Return stored media
        return stored;
    }
}
