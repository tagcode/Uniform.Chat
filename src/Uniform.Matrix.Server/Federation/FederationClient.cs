namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Keys;

/// <summary>Requests to other servers, signed with this server's key as <see cref="FederationAuth"/> describes.</summary>
/// <remarks>Errors of the other server come back as <see cref="MatrixException"/> with its error code; unreachable servers as <c>502</c>.</remarks>
public sealed class FederationClient
{
    /// <summary>Name of the HTTP client that reaches other servers.</summary>
    public const string HttpClientName = "Uniform.Matrix.Server.Federation";
    private readonly IHttpClientFactory httpClients;
    private readonly ServerResolver resolver;
    private readonly ServerKeys keys;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly ILogger<FederationClient> logger;

    /// <summary>Create the client.</summary>
    public FederationClient(IHttpClientFactory httpClients, ServerResolver resolver, ServerKeys keys, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, ILogger<FederationClient> logger)
    {
        // Store dependencies
        this.httpClients = httpClients;
        this.resolver = resolver;
        this.keys = keys;
        this.server = server;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Send a request to <paramref name="destination"/> and return its JSON answer.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="destination">Server name.</param>
    /// <param name="pathAndQuery">Escaped path and query, e.g. <c>/_matrix/federation/v1/query/profile?user_id=%40bob%3Ab.org</c>.</param>
    /// <param name="content">JSON body, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="signed">Whether to sign; key requests are not signed.</param>
    /// <param name="timeout">How long to wait for the answer; <see cref="FederationOptions.RequestTimeout"/> when null.</param>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string destination, string pathAndQuery, JsonNode? content, CancellationToken cancellationToken, bool signed = true, TimeSpan? timeout = null)
    {
        // Assert federation is on, and another server
        if (!options.CurrentValue.Enabled) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Federation is off on this server.");
        string origin = server.CurrentValue.ServerName;
        if (destination == origin) throw new InvalidOperationException("Federation requests go to other servers.");
        ServerAddress address = await resolver.ResolveAsync(destination, cancellationToken);

        // Request signed over exactly the path and query sent
        using HttpRequestMessage request = new(method, new Uri(address.BaseAddress, pathAndQuery));
        request.Headers.Host = address.HostHeader;
        if (content is not null) request.Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json");
        if (signed) request.Headers.TryAddWithoutValidation("Authorization", FederationAuth.Sign(method.Method, pathAndQuery, origin, destination, content, keys.KeyId, keys.Secret).ToString());

        // Send within the time limit
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? options.CurrentValue.RequestTimeout);
        string body;
        HttpStatusCode status;
        try
        {
            using HttpResponseMessage response = await httpClients.CreateClient(HttpClientName).SendAsync(request, limit.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(limit.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Cannot reach {Destination}: {Error}", destination, ex.Message);
            throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, $"Cannot reach {destination}.");
        }

        // Errors keep the other server's code; client errors also its status
        JsonNode? json = Parse(body);
        if ((int)status is >= 200 and < 300) return json;
        string errorCode = json?["errcode"]?.GetValue<string>() ?? ErrorCodes.Unknown;
        string message = json?["error"]?.GetValue<string>() ?? $"{destination} answered {(int)status}.";
        // On error throw
        throw new MatrixException((int)status is >= 400 and < 500 ? status : HttpStatusCode.BadGateway, errorCode, message);
    }

    /// <summary>Send a GET to <paramref name="destination"/> and hand a successful answer to <paramref name="read"/> while its body streams, e.g. to store media; errors come back as <see cref="MatrixException"/> as in <see cref="SendAsync"/>.</summary>
    /// <param name="destination">Server name.</param>
    /// <param name="pathAndQuery">Escaped path and query.</param>
    /// <param name="read">Reads the answer; it runs within the time limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="signed">Whether to sign; the legacy media downloads are not signed.</param>
    public async Task<T> DownloadAsync<T>(string destination, string pathAndQuery, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken, bool signed = true)
    {
        // Assert federation is on, and another server
        if (!options.CurrentValue.Enabled) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Federation is off on this server.");
        string origin = server.CurrentValue.ServerName;
        if (destination == origin) throw new InvalidOperationException("Federation requests go to other servers.");
        ServerAddress address = await resolver.ResolveAsync(destination, cancellationToken);

        // Request signed over exactly the path and query sent
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(address.BaseAddress, pathAndQuery));
        request.Headers.Host = address.HostHeader;
        if (signed) request.Headers.TryAddWithoutValidation("Authorization", FederationAuth.Sign("GET", pathAndQuery, origin, destination, null, keys.KeyId, keys.Secret).ToString());

        // Headers first, then the body to the reader, all within the time limit
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.CurrentValue.RequestTimeout);
        try
        {
            using HttpResponseMessage response = await httpClients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode) return await read(response, timeout.Token);
            JsonNode? json = Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            HttpStatusCode status = response.StatusCode;
            // On error throw the other server's code
            throw new MatrixException((int)status is >= 400 and < 500 ? status : HttpStatusCode.BadGateway, json?["errcode"]?.GetValue<string>() ?? ErrorCodes.Unknown, json?["error"]?.GetValue<string>() ?? $"{destination} answered {(int)status}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Cannot download from {Destination}: {Error}", destination, ex.Message);
            throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, $"Cannot reach {destination}.");
        }
    }

    /// <summary>Return the HTTP handler of the federation client: it connects only to public addresses unless <see cref="FederationOptions.AllowPrivateAddresses"/> is on.</summary>
    public static SocketsHttpHandler CreateHandler(IOptionsMonitor<FederationOptions> options)
    {
        // Few redirects, for well-known
        SocketsHttpHandler handler = PublicAddresses.CreateHandler(() => options.CurrentValue.AllowPrivateAddresses, "Federation:AllowPrivateAddresses");
        handler.MaxAutomaticRedirections = 5;
        // Return handler
        return handler;
    }

    /// <summary>Return the body as JSON, or null when it is not.</summary>
    private static JsonNode? Parse(string body)
    {
        // Lenient: error pages are not JSON
        try
        {
            return body.Length == 0 ? null : JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
