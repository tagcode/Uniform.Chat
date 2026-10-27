namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Media;

/// <summary>Tests of URL previews, with a fake website behind the server's preview client.</summary>
public class UrlPreviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Image = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    /// <summary>A fake website: a page with OpenGraph tags and a relative image, a page with only a title, and the image; counts requests.</summary>
    private sealed class Website : HttpMessageHandler
    {
        /// <summary>Requests so far.</summary>
        public int Requests;

        /// <summary>Answer by path.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Pages and the image
            Interlocked.Increment(ref Requests);
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/article" => Html("""<html><head><meta property="og:title" content="Tomatoes"><meta property="og:description" content="How to grow them"><meta property="og:image" content="/cat.png"><title>ignored</title></head><body></body></html>"""),
                "/plain" => Html("<html><head><title>Just a title</title><meta name=\"description\" content=\"From the page\"></head></html>"),
                "/cat.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) { Headers = { ContentType = new("image/png") } } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            // Return answer
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        /// <summary>Return an HTML response.</summary>
        private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    }

    /// <summary>Start a server with previews on or off and the fake website; return it with a logged-in user.</summary>
    private static async Task<(TestHomeserver Server, MatrixClient Client)> StartAsync(Website website, bool enabled)
    {
        // Server and user
        TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open", ["UrlPreviews:Enabled"] = enabled ? "true" : "false" }, configureServices: services => services.AddHttpClient(UrlPreviewService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => website));
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync("alice", "password", cancellationToken: Ct);
        // Return both
        return (server, client);
    }

    /// <summary>A page previews with its OpenGraph keys and its image stored as media; a page without them uses its title and description; previews are cached.</summary>
    [Fact]
    public async Task PreviewPages()
    {
        // Arrange
        Website website = new();
        (TestHomeserver server, MatrixClient alice) = await StartAsync(website, enabled: true);
        await using TestHomeserver running = server;

        // Act
        JsonElement article = await alice.GetUrlPreviewAsync(new Uri("https://site.example/article"), Ct);
        int requestsAfterFirst = website.Requests;
        JsonElement again = await alice.GetUrlPreviewAsync(new Uri("https://site.example/article"), Ct);
        JsonElement plain = await alice.GetUrlPreviewAsync(new Uri("https://site.example/plain"), Ct);

        // Assert: keys, the stored image, and the cache
        Assert.Equal("Tomatoes", article.GetProperty("og:title").GetString());
        Assert.Equal("How to grow them", article.GetProperty("og:description").GetString());
        Assert.Equal(Image.Length, article.GetProperty("matrix:image:size").GetInt32());
        MxcUri image = MxcUri.Parse(article.GetProperty("og:image").GetString()!);
        using MediaDownload download = await alice.DownloadMediaAsync(image, Ct);
        using MemoryStream bytes = new();
        await download.Content.CopyToAsync(bytes, Ct);
        Assert.Equal(Image, bytes.ToArray());
        Assert.Equal(2, requestsAfterFirst);
        Assert.Equal(article.GetProperty("og:image").GetString(), again.GetProperty("og:image").GetString());
        Assert.Equal("Just a title", plain.GetProperty("og:title").GetString());
        Assert.Equal("From the page", plain.GetProperty("og:description").GetString());
    }

    /// <summary>Previews are off by default, and bad addresses and failing pages give errors.</summary>
    [Fact]
    public async Task RefuseWhenOffOrBad()
    {
        // Arrange: off, and on
        (TestHomeserver off, MatrixClient offClient) = await StartAsync(new Website(), enabled: false);
        await using TestHomeserver offRunning = off;
        (TestHomeserver on, MatrixClient onClient) = await StartAsync(new Website(), enabled: true);
        await using TestHomeserver onRunning = on;

        // Act
        MatrixException disabled = await Assert.ThrowsAsync<MatrixException>(() => offClient.GetUrlPreviewAsync(new Uri("https://site.example/article"), Ct));
        MatrixException notWeb = await Assert.ThrowsAsync<MatrixException>(() => onClient.GetUrlPreviewAsync(new Uri("ftp://site.example/file"), Ct));
        MatrixException missing = await Assert.ThrowsAsync<MatrixException>(() => onClient.GetUrlPreviewAsync(new Uri("https://site.example/nothing"), Ct));

        // Assert
        Assert.Equal(ErrorCodes.Unrecognized, disabled.Errcode);
        Assert.Equal(ErrorCodes.InvalidParam, notWeb.Errcode);
        Assert.Equal(HttpStatusCode.BadGateway, missing.StatusCode);
    }

    /// <summary>The preview client refuses private addresses unless the setting allows them.</summary>
    [Fact]
    public async Task RefusePrivateAddresses()
    {
        // Arrange: the real handler, private addresses not allowed
        using HttpClient http = new(PublicAddresses.CreateHandler(() => false, "UrlPreviews:AllowPrivateAddresses"));

        // Act
        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://10.1.2.3/", Ct));

        // Assert
        Assert.Contains("private address", refused.ToString());
    }
}
