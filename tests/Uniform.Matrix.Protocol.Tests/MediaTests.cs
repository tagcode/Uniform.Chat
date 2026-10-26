namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="MxcUri"/> and media endpoints.</summary>
public class MediaTests
{
    private static readonly MatrixSession Session = new() { Homeserver = new Uri("https://hs.example/"), UserId = "@me:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Valid content URIs split into server and media ID.</summary>
    [Fact]
    public void CanParseMxcUri()
    {
        // Act
        MxcUri uri = MxcUri.Parse("mxc://matrix.org/AbC123");

        // Assert
        Assert.Equal(("matrix.org", "AbC123"), (uri.ServerName, uri.MediaId));
        Assert.Equal("mxc://matrix.org/AbC123", uri.ToString());
    }

    /// <summary>Malformed content URIs are rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("https://matrix.org/abc")]
    [InlineData("mxc://matrix.org")]
    [InlineData("mxc://matrix.org/")]
    [InlineData("mxc:///abc")]
    [InlineData("mxc://matrix.org/a/b")]
    public void CanRejectInvalidMxcUri(string? text)
    {
        // Act and assert
        Assert.False(MxcUri.TryParse(text, out _));
    }

    /// <summary>Upload posts raw bytes with content type and file name, and survives a rate-limit retry.</summary>
    [Fact]
    public async Task CanUploadMediaWithRetry()
    {
        // Arrange: first attempt rate limited
        int calls = 0;
        StubHandler handler = new(_ => ++calls == 1
            ? StubHandler.Json(HttpStatusCode.TooManyRequests, """{"errcode":"M_LIMIT_EXCEEDED","retry_after_ms":0}""")
            : StubHandler.Json(HttpStatusCode.OK, """{"content_uri":"mxc://hs.example/new"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);
        using MemoryStream file = new("hello"u8.ToArray());

        // Act
        MxcUri uri = await client.UploadMediaAsync(file, "text/plain", "hello.txt", Ct);

        // Assert
        Assert.Equal("mxc://hs.example/new", uri.ToString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("hello", r.Body));
        Assert.Equal("/_matrix/media/v3/upload?filename=hello.txt", handler.Requests[1].Request.RequestUri!.PathAndQuery);
        Assert.Equal("text/plain", handler.Requests[1].Request.Content!.Headers.ContentType!.MediaType);
    }

    /// <summary>Thumbnails use the authenticated endpoint with size and method.</summary>
    [Fact]
    public async Task CanDownloadThumbnail()
    {
        // Arrange
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) });
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        using MediaDownload thumb = await client.DownloadThumbnailAsync(MxcUri.Parse("mxc://hs.example/abc"), 320, 240, cancellationToken: Ct);

        // Assert
        Assert.Equal("/_matrix/client/v1/media/thumbnail/hs.example/abc?width=320&height=240&method=scale", handler.Requests[0].Request.RequestUri!.PathAndQuery);
        Assert.Equal(9, thumb.Content.ReadByte());
    }
}
