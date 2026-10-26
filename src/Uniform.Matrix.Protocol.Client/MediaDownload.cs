namespace Uniform.Matrix.Protocol.Client;

/// <summary>Streamed media from <see cref="MatrixClient.DownloadMediaAsync"/>; dispose to release the connection.</summary>
public sealed class MediaDownload : IDisposable
{
    private readonly HttpResponseMessage response;

    /// <summary>Wrap <paramref name="response"/> and its opened <paramref name="content"/> stream.</summary>
    internal MediaDownload(HttpResponseMessage response, Stream content)
    {
        // Read headers
        this.response = response;
        Content = content;
        ContentType = response.Content.Headers.ContentType?.MediaType;
        Length = response.Content.Headers.ContentLength;
        FileName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
    }

    /// <summary>Media bytes.</summary>
    public Stream Content { get; }
    /// <summary>MIME type, e.g. <c>image/png</c>.</summary>
    public string? ContentType { get; }
    /// <summary>File name given at upload.</summary>
    public string? FileName { get; }
    /// <summary>Size in bytes when the server sent it.</summary>
    public long? Length { get; }

    /// <summary>Close <see cref="Content"/> and the response.</summary>
    public void Dispose()
    {
        // Release stream and connection
        Content.Dispose();
        response.Dispose();
    }
}
