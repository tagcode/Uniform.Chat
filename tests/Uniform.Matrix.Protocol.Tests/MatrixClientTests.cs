namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="MatrixClient"/>.</summary>
public class MatrixClientTests
{
    private static readonly Uri Homeserver = new("https://hs.example");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Login sends a password request and stores the session.</summary>
    [Fact]
    public async Task CanLoginWithPassword()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"user_id":"@alice:hs.example","access_token":"tok","device_id":"DEV"}"""));
        MatrixClient client = new(new HttpClient(handler), Homeserver);

        // Act
        await client.LoginWithPasswordAsync("alice", "secret", "Test", Ct);

        // Assert request
        (HttpRequestMessage request, string? body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://hs.example/_matrix/client/v3/login", request.RequestUri!.ToString());

        // Assert body
        using JsonDocument json = JsonDocument.Parse(body!);
        JsonElement root = json.RootElement;
        Assert.Equal("m.login.password", root.GetProperty("type").GetString());
        Assert.Equal("m.id.user", root.GetProperty("identifier").GetProperty("type").GetString());
        Assert.Equal("alice", root.GetProperty("identifier").GetProperty("user").GetString());
        Assert.Equal("secret", root.GetProperty("password").GetString());
        Assert.Equal("Test", root.GetProperty("initial_device_display_name").GetString());
        Assert.False(root.TryGetProperty("device_id", out _));

        // Assert session
        Assert.Equal("tok", client.AccessToken);
        Assert.Equal("@alice:hs.example", client.UserId);
        Assert.Equal("DEV", client.DeviceId);
    }

    /// <summary>Authenticated requests carry the access token as bearer.</summary>
    [Fact]
    public async Task CanSendBearerToken()
    {
        // Arrange
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHandler.Json(HttpStatusCode.OK, """{"user_id":"@a:hs.example","access_token":"tok","device_id":"D"}""")
            : StubHandler.Json(HttpStatusCode.OK, """{"user_id":"@a:hs.example","device_id":"D"}"""));
        MatrixClient client = new(new HttpClient(handler), Homeserver);
        await client.LoginWithPasswordAsync("a", "p", cancellationToken: Ct);

        // Act
        WhoAmIResponse me = await client.WhoAmIAsync(Ct);

        // Assert
        Assert.Equal("@a:hs.example", me.UserId);
        AuthenticationHeaderValue? auth = handler.Requests[1].Request.Headers.Authorization;
        Assert.Equal("Bearer", auth?.Scheme);
        Assert.Equal("tok", auth?.Parameter);
    }

    /// <summary>Authenticated request without login throws before sending.</summary>
    [Fact]
    public async Task CanRejectAuthenticatedRequestWithoutLogin()
    {
        // Arrange
        MatrixClient client = new(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), Homeserver);
        // Act and assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WhoAmIAsync(Ct));
    }

    /// <summary>Matrix error body maps to <see cref="MatrixException"/>.</summary>
    [Fact]
    public async Task CanMapMatrixErrorToMatrixException()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.TooManyRequests, """{"errcode":"M_LIMIT_EXCEEDED","error":"Too many requests","retry_after_ms":2000}"""));
        MatrixClient client = new(new HttpClient(handler), Homeserver) { MaxRateLimitRetries = 0 };

        // Act
        MatrixException ex = await Assert.ThrowsAsync<MatrixException>(() => client.LoginWithPasswordAsync("a", "p", cancellationToken: Ct));

        // Assert
        Assert.Equal("M_LIMIT_EXCEEDED", ex.Errcode);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), ex.RetryAfter);
        Assert.Null(client.AccessToken);
    }

    /// <summary>Rate-limited request is retried after the server-given delay.</summary>
    [Fact]
    public async Task CanRetryRateLimitedRequest()
    {
        // Arrange: first response rate limited via Retry-After header, second succeeds
        int calls = 0;
        StubHandler handler = new(_ =>
        {
            if (calls++ > 0) return StubHandler.Json(HttpStatusCode.OK, """{"versions":["v1.12"]}""");
            HttpResponseMessage limited = StubHandler.Json(HttpStatusCode.TooManyRequests, """{"errcode":"M_LIMIT_EXCEEDED"}""");
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });
        MatrixClient client = new(new HttpClient(handler), Homeserver);

        // Act
        VersionsResponse versions = await client.GetVersionsAsync(Ct);

        // Assert
        Assert.Equal(["v1.12"], versions.Versions);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>Rate-limited request fails after <see cref="MatrixClient.MaxRateLimitRetries"/>.</summary>
    [Fact]
    public async Task CanGiveUpAfterMaxRateLimitRetries()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.TooManyRequests, """{"errcode":"M_LIMIT_EXCEEDED","retry_after_ms":0}"""));
        MatrixClient client = new(new HttpClient(handler), Homeserver) { MaxRateLimitRetries = 2 };

        // Act and assert
        await Assert.ThrowsAsync<MatrixException>(() => client.GetVersionsAsync(Ct));
        Assert.Equal(3, handler.Requests.Count);
    }

    /// <summary>Rate limit asking for longer than <see cref="MatrixClient.MaxRateLimitDelay"/> throws at once instead of waiting.</summary>
    [Fact]
    public async Task CanFailFastOnLongRateLimit()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.TooManyRequests, """{"errcode":"M_LIMIT_EXCEEDED","retry_after_ms":123000}"""));
        MatrixClient client = new(new HttpClient(handler), Homeserver);

        // Act
        MatrixException ex = await Assert.ThrowsAsync<MatrixException>(() => client.GetVersionsAsync(Ct));

        // Assert
        Assert.Single(handler.Requests);
        Assert.Equal(TimeSpan.FromSeconds(123), ex.RetryAfter);
        Assert.Contains("retry after 123 s", ex.Message);
    }

    /// <summary>Client built from a stored session sends its token without logging in.</summary>
    [Fact]
    public async Task CanResumeStoredSession()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"user_id":"@a:hs.example"}"""));
        MatrixSession session = new() { Homeserver = Homeserver, UserId = "@a:hs.example", DeviceId = "D", AccessToken = "stored" };
        MatrixClient client = new(new HttpClient(handler), session);

        // Act
        await client.WhoAmIAsync(Ct);

        // Assert
        Assert.True(client.IsLoggedIn);
        Assert.Equal("stored", handler.Requests[0].Request.Headers.Authorization?.Parameter);
    }

    /// <summary>Non-Matrix error body maps to <see cref="HttpRequestException"/>.</summary>
    [Fact]
    public async Task CanMapNonMatrixErrorToHttpRequestException()
    {
        // Arrange
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });
        MatrixClient client = new(new HttpClient(handler), Homeserver);

        // Act
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVersionsAsync(Ct));
        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    /// <summary>Endpoint paths append to a homeserver URL that has a path.</summary>
    [Fact]
    public async Task CanPreserveHomeserverPathPrefix()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"versions":["v1.12"]}"""));
        MatrixClient client = new(new HttpClient(handler), new Uri("https://hs.example/matrix"));

        // Act
        VersionsResponse versions = await client.GetVersionsAsync(Ct);

        // Assert
        Assert.Equal(["v1.12"], versions.Versions);
        Assert.Equal("https://hs.example/matrix/_matrix/client/versions", handler.Requests[0].Request.RequestUri!.ToString());
    }
}
