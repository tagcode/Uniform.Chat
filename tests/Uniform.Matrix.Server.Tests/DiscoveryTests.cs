namespace Uniform.Matrix.Server.Tests;

using System.Net;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of versions, discovery, errors and CORS.</summary>
public class DiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The client reads the supported versions.</summary>
    [Fact]
    public async Task GetVersions()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();

        // Act
        VersionsResponse versions = await server.CreateClient().GetVersionsAsync(Ct);

        // Assert
        Assert.Contains("v1.11", versions.Versions);
    }

    /// <summary>Discovery returns the public base URL when configured, and 404 otherwise.</summary>
    [Fact]
    public async Task DiscoverPublicAddress()
    {
        // Arrange
        await using TestHomeserver configured = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:PublicBaseUrl"] = "https://matrix.test.local" });
        await using TestHomeserver unconfigured = await TestHomeserver.StartAsync();

        // Act
        Uri found = await HomeserverDiscovery.DiscoverAsync(configured.CreateHttpClient(), "localhost", Ct);
        HttpResponseMessage missing = await unconfigured.CreateHttpClient().GetAsync("/.well-known/matrix/client", Ct);

        // Assert
        Assert.Equal(new Uri("https://matrix.test.local/"), found);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>Unknown endpoints and methods are Matrix errors with M_UNRECOGNIZED.</summary>
    [Fact]
    public async Task RejectUnknownEndpoints()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();
        HttpClient http = server.CreateHttpClient();

        // Act
        HttpResponseMessage unknown = await http.GetAsync("/_matrix/client/v3/nothing", Ct);
        HttpResponseMessage method = await http.DeleteAsync("/_matrix/client/versions", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(ErrorCodes.Unrecognized, await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
        Assert.Contains(ErrorCodes.Unrecognized, await method.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Browser preflight requests are answered with the CORS headers the spec requires.</summary>
    [Fact]
    public async Task AnswerCorsPreflight()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();

        // Act
        HttpResponseMessage response = await server.CreateHttpClient().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/_matrix/client/v3/login"), Ct);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
    }
}
