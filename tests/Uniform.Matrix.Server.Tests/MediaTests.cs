namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Text;
using StbImageSharp;
using StbImageWriteSharp;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Media;

/// <summary>Tests of the media repository: uploads, downloads, limits and thumbnails, through our own client.</summary>
public class MediaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };

    /// <summary>Register a user and return its client.</summary>
    private static async Task<MatrixClient> UserAsync(TestHomeserver server, string name)
    {
        // Register logs in
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync(name, "password", cancellationToken: Ct);
        // Return client
        return client;
    }

    /// <summary>Return a PNG of the given size, opaque or with a transparent left half.</summary>
    internal static byte[] Png(int width, int height, bool transparent)
    {
        // Gradient pixels
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = 128;
                pixels[i + 3] = transparent && x < width / 2 ? (byte)0 : (byte)255;
            }
        }
        using MemoryStream output = new();
        new ImageWriter().WritePng(pixels, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        // Return encoded image
        return output.ToArray();
    }

    /// <summary>An upload is downloaded by another user with its type and name; unknown media is not found, and downloads need a login.</summary>
    [Fact]
    public async Task UploadAndDownload()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        byte[] text = Encoding.UTF8.GetBytes("hello, media");

        // Act
        MxcUri uri = await alice.UploadMediaAsync(new MemoryStream(text), "text/plain", "hello.txt", Ct);
        using MediaDownload download = await bob.DownloadMediaAsync(uri, Ct);
        using MemoryStream copy = new();
        await download.Content.CopyToAsync(copy, Ct);

        // Assert
        Assert.Equal(TestHomeserver.ServerName, uri.ServerName);
        Assert.Equal(text, copy.ToArray());
        Assert.Equal("text/plain", download.ContentType);
        Assert.Equal("hello.txt", download.FileName);
        MatrixException missing = await Assert.ThrowsAsync<MatrixException>(() => bob.DownloadMediaAsync(MxcUri.Parse($"mxc://{TestHomeserver.ServerName}/AAAAAAAAAAAAAAAAAAAAAAAA"), Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Errcode);
        using HttpClient anonymous = server.CreateHttpClient();
        HttpResponseMessage refused = await anonymous.GetAsync($"_matrix/client/v1/media/download/{uri.ServerName}/{uri.MediaId}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>Download are sandboxed; types browsers could run are attachments, not shown inline.</summary>
    [Fact]
    public async Task SafeDownloadHeaders()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MxcUri page = await alice.UploadMediaAsync(new MemoryStream("<script>alert(1)</script>"u8.ToArray()), "text/html", "page.html", Ct);
        MxcUri image = await alice.UploadMediaAsync(new MemoryStream(Png(4, 4, transparent: false)), "image/png", "dot.png", Ct);
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Session!.AccessToken);

        // Act
        HttpResponseMessage pageResponse = await http.GetAsync($"_matrix/client/v1/media/download/{page.ServerName}/{page.MediaId}", Ct);
        HttpResponseMessage imageResponse = await http.GetAsync($"_matrix/client/v1/media/download/{image.ServerName}/{image.MediaId}/renamed.png", Ct);

        // Assert
        Assert.Equal("attachment", pageResponse.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox", string.Join(' ', pageResponse.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join(' ', pageResponse.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("inline", imageResponse.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("renamed.png", imageResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    /// <summary>Upload past the configured limit are refused, and the limit is announced.</summary>
    [Fact]
    public async Task UploadLimit()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?>(Open) { ["Media:MaxUploadSize"] = "10" });
        MatrixClient alice = await UserAsync(server, "alice");
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Session!.AccessToken);

        // Act
        MatrixException tooLarge = await Assert.ThrowsAsync<MatrixException>(() => alice.UploadMediaAsync(new MemoryStream(new byte[11]), "application/octet-stream", cancellationToken: Ct));
        MxcUri small = await alice.UploadMediaAsync(new MemoryStream(new byte[10]), "application/octet-stream", cancellationToken: Ct);
        string config = await http.GetStringAsync("_matrix/client/v1/media/config", Ct);

        // Assert
        Assert.Equal("M_TOO_LARGE", tooLarge.Errcode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.NotNull(small.MediaId);
        Assert.Contains("\"m.upload.size\":10", config);
    }

    /// <summary>Thumbnails come in standard sizes, cropped or scaled, PNG with transparency and JPEG without; files that are not images get none.</summary>
    [Fact]
    public async Task Thumbnails()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MxcUri photo = await alice.UploadMediaAsync(new MemoryStream(Png(400, 300, transparent: false)), "image/png", cancellationToken: Ct);
        MxcUri logo = await alice.UploadMediaAsync(new MemoryStream(Png(64, 64, transparent: true)), "image/png", cancellationToken: Ct);
        MxcUri text = await alice.UploadMediaAsync(new MemoryStream("text"u8.ToArray()), "text/plain", cancellationToken: Ct);

        // Act
        (ImageResult cropped, string? croppedType) = await ThumbnailAsync(alice, photo, 90, 90, crop: true);
        (ImageResult scaled, string? scaledType) = await ThumbnailAsync(alice, photo, 300, 200, crop: false);
        (ImageResult again, _) = await ThumbnailAsync(alice, photo, 300, 200, crop: false);
        (ImageResult small, string? smallType) = await ThumbnailAsync(alice, logo, 640, 480, crop: false);

        // Assert
        Assert.Equal((96, 96), (cropped.Width, cropped.Height));
        Assert.Equal("image/jpeg", croppedType);
        Assert.Equal((320, 240), (scaled.Width, scaled.Height));
        Assert.Equal(scaled.Data, again.Data);
        Assert.Equal((64, 64), (small.Width, small.Height));
        Assert.Equal("image/png", smallType);
        MatrixException notImage = await Assert.ThrowsAsync<MatrixException>(() => alice.DownloadThumbnailAsync(text, 96, 96, cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
    }

    /// <summary>Requests map to the smallest standard size that covers them, else the largest.</summary>
    [Theory]
    [InlineData(10, 10, "crop", 32, 32, "crop")]
    [InlineData(50, 50, "crop", 96, 96, "crop")]
    [InlineData(500, 500, "crop", 96, 96, "crop")]
    [InlineData(100, 100, "scale", 320, 240, "scale")]
    [InlineData(700, 500, "scale", 800, 600, "scale")]
    [InlineData(2000, 2000, "scale", 800, 600, "scale")]
    [InlineData(100, 100, "fancy", 320, 240, "scale")]
    public void ChooseThumbnailSize(int width, int height, string method, int expectedWidth, int expectedHeight, string expectedMethod)
    {
        // Act
        (int Width, int Height, string Method) size = Thumbnailer.ChooseSize(width, height, method);

        // Assert
        Assert.Equal((expectedWidth, expectedHeight, expectedMethod), size);
    }

    /// <summary>Download and decode a thumbnail.</summary>
    private static async Task<(ImageResult Image, string? ContentType)> ThumbnailAsync(MatrixClient client, MxcUri uri, int width, int height, bool crop)
    {
        // Download all bytes
        using MediaDownload download = await client.DownloadThumbnailAsync(uri, width, height, crop, Ct);
        using MemoryStream bytes = new();
        await download.Content.CopyToAsync(bytes, Ct);
        // Return decoded
        return (ImageResult.FromMemory(bytes.ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha), download.ContentType);
    }

    /// <summary>Created media is uploaded later by its creator only, once; a download waits for the content, or answers that it is not uploaded yet.</summary>
    [Fact]
    public async Task CreateThenUpload()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        byte[] text = Encoding.UTF8.GetBytes("later");

        // Act: reserve, try a download before the content, then upload while another download waits
        (MxcUri uri, DateTimeOffset expires) = await alice.CreateMediaAsync(Ct);
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", bob.AccessToken);
        HttpResponseMessage early = await http.GetAsync($"/_matrix/client/v1/media/download/{uri.ServerName}/{uri.MediaId}?timeout_ms=0", Ct);
        Task<HttpResponseMessage> waiting = http.GetAsync($"/_matrix/client/v1/media/download/{uri.ServerName}/{uri.MediaId}?timeout_ms=10000", Ct);
        MatrixException notOwner = await Assert.ThrowsAsync<MatrixException>(() => bob.UploadMediaToAsync(uri, new MemoryStream(text), "text/plain", cancellationToken: Ct));
        await alice.UploadMediaToAsync(uri, new MemoryStream(text), "text/plain", "later.txt", Ct);
        HttpResponseMessage late = await waiting;
        MatrixException again = await Assert.ThrowsAsync<MatrixException>(() => alice.UploadMediaToAsync(uri, new MemoryStream(text), "text/plain", cancellationToken: Ct));

        // Assert
        Assert.True(expires > DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.GatewayTimeout, early.StatusCode);
        Assert.Contains(ErrorCodes.NotYetUploaded, await early.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ErrorCodes.Forbidden, notOwner.Errcode);
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(text, await late.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(ErrorCodes.CannotOverwriteMedia, again.Errcode);
    }

    /// <summary>The unauthenticated legacy downloads are off by default, and serve files without a token when the setting allows them.</summary>
    [Fact]
    public async Task LegacyDownloads()
    {
        // Arrange: one server with the setting off, one with it on
        await using TestHomeserver off = await TestHomeserver.StartAsync(Open);
        await using TestHomeserver on = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open", ["Media:AllowUnauthenticatedDownloads"] = "true" });
        MxcUri offUri = await (await UserAsync(off, "alice")).UploadMediaAsync(new MemoryStream([1, 2, 3]), "application/octet-stream", cancellationToken: Ct);
        MxcUri onUri = await (await UserAsync(on, "alice")).UploadMediaAsync(new MemoryStream([1, 2, 3]), "application/octet-stream", cancellationToken: Ct);

        // Act: no access token
        using HttpClient offHttp = off.CreateHttpClient();
        using HttpClient onHttp = on.CreateHttpClient();
        HttpResponseMessage refused = await offHttp.GetAsync($"/_matrix/media/v3/download/{offUri.ServerName}/{offUri.MediaId}", Ct);
        HttpResponseMessage served = await onHttp.GetAsync($"/_matrix/media/v3/download/{onUri.ServerName}/{onUri.MediaId}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal([1, 2, 3], await served.Content.ReadAsByteArrayAsync(Ct));
    }
}
