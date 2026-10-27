namespace Uniform.Matrix.Protocol.Client;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

/// <summary>Thin wrapper over the Matrix <see href="https://spec.matrix.org/latest/client-server-api/">Client-Server API</see> for one homeserver and session.</summary>
/// <remarks>
/// Split into partial files by spec area:
/// <list type="bullet">
/// <item><description>MatrixClient.Auth.cs: server info, login and logout.</description></item>
/// <item><description>MatrixClient.Sync.cs: sync, sync loop and filters.</description></item>
/// <item><description>MatrixClient.Rooms.cs: room membership, creation, aliases, state and members.</description></item>
/// <item><description>MatrixClient.Events.cs: room history, sending, redaction, typing and read markers.</description></item>
/// <item><description>MatrixClient.Profile.cs: profiles and presence.</description></item>
/// <item><description>MatrixClient.AccountData.cs: account data, tags, ignored users and direct rooms.</description></item>
/// <item><description>MatrixClient.PushRules.cs: push rules and pushers.</description></item>
/// <item><description>MatrixClient.Directory.cs: public room directory and user directory.</description></item>
/// <item><description>MatrixClient.Media.cs: media upload and download, file messages.</description></item>
/// <item><description>MatrixClient.Devices.cs: devices and user-interactive auth.</description></item>
/// <item><description>MatrixClient.Keys.cs: encryption keys and to-device messages.</description></item>
/// </list>
/// </remarks>
/// <example><code><![CDATA[
/// MatrixClient client = new(http, new Uri("https://matrix.org"));
/// await client.LoginWithPasswordAsync("@alice:matrix.org", password);
/// await foreach (SyncResponse sync in client.SyncLoopAsync(cancellationToken: cancellationToken))
///     foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join)
///         foreach (ClientEvent ev in room.Timeline.Events)
///             if (ev.TryGetContent(out MessageContent? message)) Console.WriteLine($"{ev.Sender}: {message.Body}");
/// ]]></code></example>
public sealed partial class MatrixClient
{
    private readonly HttpClient http;
    private long transactionCounter;

    /// <summary>Create a logged-out client for <paramref name="homeserver"/>.</summary>
    /// <param name="http">Client used for all requests; owned by the caller.</param>
    /// <param name="homeserver">Homeserver base URL, e.g. from <see cref="HomeserverDiscovery.DiscoverAsync"/>.</param>
    public MatrixClient(HttpClient http, Uri homeserver)
    {
        // Store HTTP client
        this.http = http;
        // Ensure trailing slash
        Homeserver = homeserver.AbsoluteUri.EndsWith('/') ? homeserver : new Uri(homeserver.AbsoluteUri + "/");
    }

    /// <summary>Create a client that resumes a stored <paramref name="session"/>.</summary>
    /// <param name="http">Client used for all requests; owned by the caller.</param>
    /// <param name="session">Session from an earlier <see cref="LoginWithPasswordAsync"/>.</param>
    public MatrixClient(HttpClient http, MatrixSession session) : this(http, session.Homeserver)
    {
        // Resume session
        Session = session;
    }

    /// <summary>Base URL with a trailing slash, so relative endpoint paths append instead of replacing.</summary>
    public Uri Homeserver { get; }
    /// <summary>Current session; null when logged out.</summary>
    public MatrixSession? Session { get; private set; }
    /// <summary>Access token of <see cref="Session"/>.</summary>
    public string? AccessToken => Session?.AccessToken;
    /// <summary>Full user ID of <see cref="Session"/>.</summary>
    public string? UserId => Session?.UserId;
    /// <summary>Device ID of <see cref="Session"/>.</summary>
    public string? DeviceId => Session?.DeviceId;
    /// <summary>Whether <see cref="Session"/> is set.</summary>
    public bool IsLoggedIn => Session is not null;
    /// <summary>Times a <see href="https://spec.matrix.org/latest/client-server-api/#rate-limiting">rate-limited</see> request is retried before <see cref="MatrixException"/> is thrown.</summary>
    public int MaxRateLimitRetries { get; set; } = 3;
    /// <summary>Longest server-requested wait that is retried; longer waits throw <see cref="MatrixException"/> at once so callers are not blocked for minutes.</summary>
    public TimeSpan MaxRateLimitDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Return <see cref="Session"/>; throw <see cref="InvalidOperationException"/> when logged out.</summary>
    private MatrixSession RequireSession() => Session ?? throw new InvalidOperationException("Not logged in.");

    /// <summary>Unique per access token, see <see href="https://spec.matrix.org/latest/client-server-api/#transaction-identifiers">transaction identifiers</see>; timestamp keeps IDs unique across restarts.</summary>
    private string NewTransactionId() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Interlocked.Increment(ref transactionCounter)}";

    /// <summary>Percent-escapes a path segment or queries value, e.g. <c>!</c>, <c>#</c> and <c>:</c> of Matrix IDs.</summary>
    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    /// <summary>Append <paramref name="parameters"/> with non-null values to <paramref name="path"/> as a query string.</summary>
    private static string WithQuery(string path, IEnumerable<(string Name, string? Value)> parameters)
    {
        // Join set parameters
        string query = string.Join('&', parameters.Where(p => p.Value is not null).Select(p => $"{Escape(p.Name)}={Escape(p.Value!)}"));
        // Return path with query
        return query.Length == 0 ? path : $"{path}?{query}";
    }

    /// <summary>Send a request and discard the response body.</summary>
    private Task SendAsync(HttpMethod method, string path, object? body, bool authenticated, CancellationToken cancellationToken) =>
        SendAsync<JsonElement>(method, path, body, authenticated, cancellationToken);

    /// <summary>Send a request, retries rate limiting and return the body as <typeparamref name="T"/>; throw <see cref="MatrixException"/> or <see cref="HttpRequestException"/> on errors.</summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken cancellationToken)
    {
        // Send JSON body
        using HttpResponseMessage response = await SendRawAsync(method, path, () => body is null ? null : JsonContent.Create(body, body.GetType(), options: MatrixJson.Options), authenticated, HttpCompletionOption.ResponseContentRead, cancellationToken);
        // Return parsed body
        return await response.Content.ReadFromJsonAsync<T>(MatrixJson.Options, cancellationToken) ?? throw new InvalidOperationException($"Empty response from {path}");
    }

    /// <summary>Send a request with content from <paramref name="createContent"/>, retrying rate limiting; return the successful response, which the caller disposes.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path relative to <see cref="Homeserver"/>, with query.</param>
    /// <param name="createContent">Creates the body for each attempt, as content cannot be resent; null result for no body.</param>
    /// <param name="authenticated">Whether to send the access token.</param>
    /// <param name="completion">When the response is returned; <see cref="HttpCompletionOption.ResponseHeadersRead"/> to stream large bodies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, Func<HttpContent?> createContent, bool authenticated, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        // Assert session
        if (authenticated) RequireSession();

        // Send, retrying while rate limited
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = CreateRequest(method, path, createContent(), authenticated);
            HttpResponseMessage response = await http.SendAsync(request, completion, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            Exception error;
            using (response) error = await ToExceptionAsync(response, cancellationToken);
            if (error is not MatrixException { Errcode: ErrorCodes.LimitExceeded } limited || attempt >= MaxRateLimitRetries) throw error;
            TimeSpan delay = limited.RetryAfter ?? TimeSpan.FromSeconds(1 << attempt);
            if (delay > MaxRateLimitDelay) throw error;
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>Build a request with <paramref name="content"/> and, when <paramref name="authenticated"/>, the bearer token.</summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content, bool authenticated)
    {
        // Build request
        HttpRequestMessage request = new(method, new Uri(Homeserver, path)) { Content = content };
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        // Return request
        return request;
    }

    /// <summary>Map an error response to <see cref="MatrixException"/>, or <see cref="HttpRequestException"/> when the body is not a Matrix error.</summary>
    private static async Task<Exception> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Used when body is not a Matrix error
        HttpRequestException fallback = new($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        // Parse body as JSON
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Not a Matrix error body, e.g. HTML from a proxy.
            return fallback;
        }

        // Interactive auth asks for more stages; such bodies need not have an errcode
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return fallback;
            if (response.StatusCode == HttpStatusCode.Unauthorized && root.TryGetProperty("flows", out _)) return new UserInteractiveAuthException(root.Deserialize<UserInteractiveAuthResponse>(MatrixJson.Options)!);
            if (!root.TryGetProperty("errcode", out _)) return fallback;

            // Return Matrix error; Retry-After header is the newer form of retry_after_ms
            ErrorResponse error = root.Deserialize<ErrorResponse>(MatrixJson.Options)!;
            TimeSpan? retryAfter = error.RetryAfterMs is long ms ? TimeSpan.FromMilliseconds(ms) : response.Headers.RetryAfter?.Delta;
            return new MatrixException(response.StatusCode, error.Errcode, error.Error, retryAfter);
        }
    }
}
