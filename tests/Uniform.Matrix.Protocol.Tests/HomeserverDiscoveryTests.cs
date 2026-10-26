namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using Uniform.Matrix.Protocol.Client;

/// <summary>Unit tests for <see cref="HomeserverDiscovery"/>.</summary>
public class HomeserverDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Base URL is taken from the well-known file.</summary>
    [Fact]
    public async Task CanUseBaseUrlFromWellKnown()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"m.homeserver":{"base_url":"https://matrix.example.org"}}"""));

        // Act
        Uri uri = await HomeserverDiscovery.DiscoverAsync(new HttpClient(handler), "example.org", Ct);

        // Assert
        Assert.Equal("https://example.org/.well-known/matrix/client", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(new Uri("https://matrix.example.org"), uri);
    }

    /// <summary>Missing well-known file falls back to the server name.</summary>
    [Fact]
    public async Task CanFallBackToServerNameWhenNotFound()
    {
        // Arrange
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        Uri uri = await HomeserverDiscovery.DiscoverAsync(new HttpClient(handler), "example.org", Ct);

        // Assert
        Assert.Equal(new Uri("https://example.org/"), uri);
    }

    /// <summary>Malformed well-known file is rejected.</summary>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"m.homeserver":{}}""")]
    [InlineData("""{"m.homeserver":{"base_url":"not a url"}}""")]
    [InlineData("""not json""")]
    public async Task CanRejectInvalidWellKnown(string json)
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, json));
        // Act and assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => HomeserverDiscovery.DiscoverAsync(new HttpClient(handler), "example.org", Ct));
    }
}
