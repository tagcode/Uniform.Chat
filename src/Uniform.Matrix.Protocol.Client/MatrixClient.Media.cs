namespace Uniform.Matrix.Protocol.Client;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

// Media upload and download.
public sealed partial class MatrixClient
{
    /// <summary>Download media via the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1mediadownloadservernamemediaid">authenticated media</see> endpoint, streaming the body.</summary>
    /// <example><code><![CDATA[
    /// using MediaDownload media = await client.DownloadMediaAsync(MxcUri.Parse(message.Url!));
    /// await using FileStream file = File.Create(media.FileName ?? "download");
    /// await media.Content.CopyToAsync(file);
    /// ]]></code></example>
    public async Task<MediaDownload> DownloadMediaAsync(MxcUri uri, CancellationToken cancellationToken = default)
    {
        // Request headers only, so large files stream
        HttpResponseMessage response = await SendRawAsync(HttpMethod.Get, $"_matrix/client/v1/media/download/{Escape(uri.ServerName)}/{Escape(uri.MediaId)}", () => null, authenticated: true, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // Return stream owning the response
        return new MediaDownload(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    /// <summary>Download a server-made <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1mediathumbnailservernamemediaid">thumbnail</see> of about <paramref name="width"/>×<paramref name="height"/> pixels; servers may return the original for small images.</summary>
    /// <param name="uri">Content URI of an image or video.</param>
    /// <param name="width">Desired width in pixels.</param>
    /// <param name="height">Desired height in pixels.</param>
    /// <param name="crop">Whether to crop to the exact size instead of scaling to fit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MediaDownload> DownloadThumbnailAsync(MxcUri uri, int width, int height, bool crop = false, CancellationToken cancellationToken = default)
    {
        // Request headers only, so the body streams
        string path = WithQuery($"_matrix/client/v1/media/thumbnail/{Escape(uri.ServerName)}/{Escape(uri.MediaId)}", [("width", width.ToString()), ("height", height.ToString()), ("method", crop ? "crop" : "scale")]);
        HttpResponseMessage response = await SendRawAsync(HttpMethod.Get, path, () => null, authenticated: true, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // Return stream owning the response
        return new MediaDownload(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixmediav3upload">Uploads</see> media and returns its content URI for e.g. <see cref="MessageContent.Url"/>.</summary>
    /// <param name="content">Media bytes; buffered in memory when not seekable, as rate-limit retries resend it.</param>
    /// <param name="contentType">MIME type, e.g. <c>image/png</c>.</param>
    /// <param name="fileName">File name to store with the media.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MxcUri> UploadMediaAsync(Stream content, string contentType, string? fileName = null, CancellationToken cancellationToken = default)
    {
        // Make body resendable
        Stream body = content;
        if (!content.CanSeek)
        {
            body = new MemoryStream();
            await content.CopyToAsync(body, cancellationToken);
        }
        long start = content.CanSeek ? content.Position : 0;

        // Upload
        string path = WithQuery("_matrix/media/v3/upload", [("filename", fileName)]);
        using HttpResponseMessage response = await SendRawAsync(HttpMethod.Post, path, () => CreateStreamContent(body, start, contentType), authenticated: true, HttpCompletionOption.ResponseContentRead, cancellationToken);
        UploadResponse result = await response.Content.ReadFromJsonAsync<UploadResponse>(MatrixJson.Options, cancellationToken) ?? throw new InvalidOperationException("Empty upload response");
        // Return content URI
        return MxcUri.Parse(result.ContentUri);
    }

    /// <summary>Return the server's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1mediapreview_url">preview</see> of a web page as JSON: <c>og:title</c>, <c>og:description</c>, and <c>og:image</c> as an <c>mxc://</c> URI with <c>matrix:image:size</c>; throws <see cref="MatrixException"/> when the server has previews off.</summary>
    public Task<JsonElement> GetUrlPreviewAsync(Uri url, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, WithQuery("_matrix/client/v1/media/preview_url", [("url", url.AbsoluteUri)]), body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixmediav1create">Reserve a content URI</see> whose content is uploaded later with <see cref="UploadMediaToAsync"/>, e.g. to send a message before a large file is up; return it and when it expires.</summary>
    public async Task<(MxcUri ContentUri, DateTimeOffset ExpiresAt)> CreateMediaAsync(CancellationToken cancellationToken = default)
    {
        // Reserve
        CreateMediaResponse result = await SendAsync<CreateMediaResponse>(HttpMethod.Post, "_matrix/media/v1/create", new { }, authenticated: true, cancellationToken);
        // Return URI and expiry
        return (MxcUri.Parse(result.ContentUri), DateTimeOffset.FromUnixTimeMilliseconds(result.UnusedExpiresAt ?? 0));
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixmediav3uploadservernamemediaid">Upload the content</see> of a URI from <see cref="CreateMediaAsync"/>; only once, by the user who created it.</summary>
    /// <param name="contentUri">The reserved URI.</param>
    /// <param name="content">Media bytes; buffered in memory when not seekable, as rate-limit retries resend it.</param>
    /// <param name="contentType">MIME type, e.g. <c>image/png</c>.</param>
    /// <param name="fileName">File name to store with the media.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UploadMediaToAsync(MxcUri contentUri, Stream content, string contentType, string? fileName = null, CancellationToken cancellationToken = default)
    {
        // Make body resendable
        Stream body = content;
        if (!content.CanSeek)
        {
            body = new MemoryStream();
            await content.CopyToAsync(body, cancellationToken);
        }
        long start = content.CanSeek ? content.Position : 0;

        // Upload to the reserved URI
        string path = WithQuery($"_matrix/media/v3/upload/{Escape(contentUri.ServerName)}/{Escape(contentUri.MediaId)}", [("filename", fileName)]);
        // Done
        using HttpResponseMessage response = await SendRawAsync(HttpMethod.Put, path, () => CreateStreamContent(body, start, contentType), authenticated: true, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    /// <summary>Upload <paramref name="content"/> and send it to <paramref name="roomId"/> as an image, video, audio or file message by <paramref name="contentType"/>; return the event ID.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="content">File bytes.</param>
    /// <param name="fileName">File name shown to others.</param>
    /// <param name="contentType">MIME type, e.g. <c>image/png</c>.</param>
    /// <param name="transactionId">Reuse when retrying the same send; generated when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> SendFileAsync(string roomId, Stream content, string fileName, string contentType, string? transactionId = null, CancellationToken cancellationToken = default)
    {
        // Upload
        long? size = content.CanSeek ? content.Length - content.Position : null;
        MxcUri uri = await UploadMediaAsync(content, contentType, fileName, cancellationToken);
        // Send message referring to the upload
        return await SendMessageAsync(roomId, CreateFileContent(uri, fileName, contentType, size), transactionId, cancellationToken);
    }

    /// <summary>Build file message content for uploaded media, picking the message type from <paramref name="contentType"/>.</summary>
    public static MessageContent CreateFileContent(MxcUri uri, string fileName, string contentType, long? size) =>
        new() { MessageType = GetFileMessageType(contentType), Body = fileName, Filename = fileName, Url = uri.ToString(), Info = new MediaInfo { Mimetype = contentType, Size = size } };

    /// <summary>Build file message content for an encrypted upload, for encrypted rooms.</summary>
    public static MessageContent CreateFileContent(EncryptedFile file, string fileName, string contentType, long? size) =>
        new() { MessageType = GetFileMessageType(contentType), Body = fileName, Filename = fileName, File = file, Info = new MediaInfo { Mimetype = contentType, Size = size } };

    /// <summary>Pick the message type of an attachment from its MIME type.</summary>
    private static string GetFileMessageType(string contentType)
    {
        // Return type by the top-level MIME type
        return contentType.Split('/')[0] switch
        {
            "image" => MessageTypes.Image,
            "video" => MessageTypes.Video,
            "audio" => MessageTypes.Audio,
            _ => MessageTypes.File,
        };
    }

    /// <summary>Wrap <paramref name="body"/> from <paramref name="start"/> without letting the request dispose it.</summary>
    private static HttpContent CreateStreamContent(Stream body, long start, string contentType)
    {
        // Rewind for this attempt
        body.Position = start;
        StreamContent content = new(new NonDisposingStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Return content
        return content;
    }

    /// <summary>Stream wrapper that ignores dispose, so retries can resend the caller's stream.</summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        /// <summary>Whether the inner stream can be read.</summary>
        public override bool CanRead => inner.CanRead;
        /// <summary>Whether the inner stream can seek.</summary>
        public override bool CanSeek => inner.CanSeek;
        /// <summary>Always false.</summary>
        public override bool CanWrite => false;
        /// <summary>Length of the inner stream.</summary>
        public override long Length => inner.Length;
        /// <summary>Position of the inner stream.</summary>
        public override long Position { get => inner.Position; set => inner.Position = value; }
        /// <summary>No-op.</summary>
        public override void Flush() { }
        /// <summary>Read from the inner stream.</summary>
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        /// <summary>Read from the inner stream.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        /// <summary>Seek the inner stream.</summary>
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        /// <summary>Not supported.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>Not supported.</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
