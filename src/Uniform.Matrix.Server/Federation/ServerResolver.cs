namespace Uniform.Matrix.Server.Federation;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;

/// <summary>Where to reach a server: the base address, whose host the TLS certificate must be valid for, and the <c>Host</c> header.</summary>
/// <param name="BaseAddress">E.g. <c>https://matrix.example.org:8448/</c>.</param>
/// <param name="HostHeader">E.g. <c>matrix.example.org</c>, or with the port when the name has one.</param>
public sealed record ServerAddress(Uri BaseAddress, string HostHeader);

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#resolving-server-names">Resolve server names</see> to addresses: IP literals and names with a port directly, otherwise through <c>/.well-known/matrix/server</c> delegation, then SRV records (<c>_matrix-fed._tcp</c>, and the older <c>_matrix._tcp</c>), else port 8448.</summary>
/// <remarks>With an SRV record, the connection goes to the record's target while the <c>Host</c> header, and with it the name the TLS certificate must be valid for, stays the server name. Results are cached for a day when delegation was found, an hour when not.</remarks>
public sealed class ServerResolver
{
    private static readonly TimeSpan FoundLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan NotFoundLifetime = TimeSpan.FromHours(1);
    private const int MaxWellKnownBytes = 64 * 1024;
    private readonly IHttpClientFactory httpClients;
    private readonly SrvLookup srv;
    private readonly TimeProvider time;
    private readonly ILogger<ServerResolver> logger;
    private readonly ConcurrentDictionary<string, (ServerAddress Address, DateTimeOffset Expires)> cache = new();

    /// <summary>Create the resolver.</summary>
    public ServerResolver(IHttpClientFactory httpClients, SrvLookup srv, TimeProvider time, ILogger<ServerResolver> logger)
    {
        // Store dependencies
        this.httpClients = httpClients;
        this.srv = srv;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Return where to reach <paramref name="serverName"/>; throw <c>M_INVALID_PARAM</c> for invalid names.</summary>
    public async Task<ServerAddress> ResolveAsync(string serverName, CancellationToken cancellationToken)
    {
        // Assert a valid name; cached answers first
        if (!ServerNames.TryParse(serverName, out string? host, out int? port)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Invalid server name {serverName}.");
        if (cache.TryGetValue(serverName, out (ServerAddress Address, DateTimeOffset Expires) cached) && cached.Expires > time.GetUtcNow()) return cached.Address;

        // IP literals and explicit ports need no lookup
        if (ServerNames.IsIpLiteral(host) || port is not null) return Remember(serverName, Address(host, port ?? ServerNames.DefaultPort, serverName), FoundLifetime);

        // Delegation through well-known: IP literals and explicit ports directly, else the delegated name's SRV records or default port
        string? delegated = await WellKnownAsync(host, cancellationToken);
        if (delegated is not null && ServerNames.TryParse(delegated, out string? delegatedHost, out int? delegatedPort))
        {
            if (ServerNames.IsIpLiteral(delegatedHost) || delegatedPort is not null) return Remember(serverName, Address(delegatedHost, delegatedPort ?? ServerNames.DefaultPort, delegatedPort is null ? delegatedHost : delegated), FoundLifetime);
            return Remember(serverName, await SrvAddressAsync(delegatedHost, cancellationToken) ?? Address(delegatedHost, ServerNames.DefaultPort, delegatedHost), FoundLifetime);
        }

        // SRV records of the name itself
        if (await SrvAddressAsync(host, cancellationToken) is ServerAddress service) return Remember(serverName, service, FoundLifetime);
        // Return the default port of the name itself
        return Remember(serverName, Address(host, ServerNames.DefaultPort, host), NotFoundLifetime);
    }

    /// <summary>Return the address of the best SRV record of <paramref name="host"/>, the current name first, then the deprecated one; null when it has none. The <c>Host</c> header stays <paramref name="host"/>.</summary>
    private async Task<ServerAddress?> SrvAddressAsync(string host, CancellationToken cancellationToken)
    {
        // Current, then deprecated service names
        foreach (string service in new[] { "_matrix-fed._tcp", "_matrix._tcp" })
        {
            IReadOnlyList<SrvRecord> records = await srv.LookupAsync($"{service}.{host}", cancellationToken);
            if (records.Count > 0) return Address(records[0].Target, records[0].Port, host);
        }
        // Return none
        return null;
    }

    /// <summary>Cache and return an address.</summary>
    private ServerAddress Remember(string serverName, ServerAddress address, TimeSpan lifetime)
    {
        // Until expiry
        cache[serverName] = (address, time.GetUtcNow() + lifetime);
        // Return address
        return address;
    }

    /// <summary>Return the address of a host and port.</summary>
    private static ServerAddress Address(string host, int port, string hostHeader) => new(new Uri($"https://{host}:{port}/"), hostHeader);

    /// <summary>Return the <c>m.server</c> of <c>https://host/.well-known/matrix/server</c>, or null when there is none or it cannot be read.</summary>
    private async Task<string?> WellKnownAsync(string host, CancellationToken cancellationToken)
    {
        // Small answer, redirects followed by the client
        try
        {
            HttpClient http = httpClients.CreateClient(FederationClient.HttpClientName);
            http.MaxResponseContentBufferSize = MaxWellKnownBytes;
            using HttpResponseMessage response = await http.GetAsync($"https://{host}/.well-known/matrix/server", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            // Return delegated name
            return body?["m.server"] is JsonValue value && value.TryGetValue(out string? name) ? name : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            // On error no delegation
            logger.LogDebug("No well-known delegation for {Host}: {Error}", host, ex.Message);
            return null;
        }
    }
}
