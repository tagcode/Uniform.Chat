namespace Uniform.Stun.Hosting;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Stun;
using Uniform.Turn;

/// <summary>Runs a <see cref="TurnServer"/> in a .NET host when <see cref="TurnOptions.Enabled"/> is set, with the credentials of the settings and of every <see cref="ITurnAuthenticator"/> registered in the host, e.g. a homeserver's own secret.</summary>
/// <remarks>Settings that cannot work are logged as errors and leave the server stopped, without stopping the host: a homeserver keeps serving when its TURN settings are wrong. <see cref="Error"/> tells why.</remarks>
public sealed class TurnServerHost : IHostedService, IAsyncDisposable
{
    private readonly IOptions<TurnOptions> options;
    private readonly IEnumerable<ITurnAuthenticator> authenticators;
    private readonly ILoggerFactory loggers;
    private readonly ILogger<TurnServerHost> logger;
    private readonly TimeProvider time;

    /// <summary>Create the host of the server.</summary>
    public TurnServerHost(IOptions<TurnOptions> options, IEnumerable<ITurnAuthenticator> authenticators, ILoggerFactory loggers, TimeProvider? time = null)
    {
        // Store dependencies
        this.options = options;
        this.authenticators = authenticators;
        this.loggers = loggers;
        logger = loggers.CreateLogger<TurnServerHost>();
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The running server; null when disabled or not started.</summary>
    public TurnServer? Server { get; private set; }

    /// <summary>Why the server did not start although enabled; null otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>Start the server when enabled; errors of the settings are logged and kept in <see cref="Error"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Enabled only
        TurnOptions settings = options.Value;
        if (!settings.Enabled) return;

        // Settings, credentials, then the server
        try
        {
            TurnServerOptions serverOptions = await CreateServerOptionsAsync(settings, cancellationToken);
            ITurnAuthenticator authenticator = CreateAuthenticator(settings);
            TurnServer server = new(serverOptions, authenticator, loggers.CreateLogger<TurnServer>(), time);
            await server.StartAsync(cancellationToken);
            Server = server;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure of this optional part, e.g. an unreadable certificate, leaves the host running without it
            Error = ex switch
            {
                SocketException socket => $"Cannot listen: {socket.Message}. Is another TURN server running on the port?",
                UnauthorizedAccessException => $"{ex.Message} The user running the server cannot read it.",
                _ => ex.Message,
            };
            logger.LogError("The TURN server did not start: {Error}", Error);
        }
    }

    /// <summary>Stop the server.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // End every allocation
        if (Server is not TurnServer server) return;
        Server = null;
        await server.DisposeAsync();
    }

    /// <summary>Stop the server.</summary>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    /// <summary>Return the server's options from the settings, resolving a host name of the external address.</summary>
    private async Task<TurnServerOptions> CreateServerOptionsAsync(TurnOptions settings, CancellationToken cancellationToken)
    {
        // Addresses
        IPAddress listen = ParseAddress(settings.ListenAddress, "Turn:ListenAddress");
        IPAddress? relay = settings.RelayAddress is { Length: > 0 } relayText ? ParseAddress(relayText, "Turn:RelayAddress") : null;
        (IPAddress? external, Func<CancellationToken, Task<IPAddress?>>? source) = ExternalAddress(settings.ExternalAddress, relay ?? listen);

        // TLS certificate, read again when renewed
        if (settings.TlsPort is not null && (string.IsNullOrEmpty(settings.CertificatePath) || string.IsNullOrEmpty(settings.KeyPath))) throw new InvalidOperationException("TURN over TLS (Turn:TlsPort) needs Turn:CertificatePath and Turn:KeyPath.");
        PemCertificate? certificate = settings.TlsPort is null ? null : new PemCertificate(settings.CertificatePath!, settings.KeyPath!);
        if (certificate is not null) certificate.Get();

        // Return options
        return new TurnServerOptions
        {
            Realm = string.IsNullOrEmpty(settings.Realm) ? "turn" : settings.Realm,
            ListenAddress = listen,
            Port = settings.Port,
            Tcp = settings.Tcp,
            TlsPort = settings.TlsPort,
            Certificate = certificate is null ? null : certificate.Get,
            RelayAddress = relay,
            ExternalAddress = external,
            ExternalAddressSource = source,
            AddressCheckInterval = settings.AddressCheckInterval,
            MinRelayPort = settings.MinRelayPort,
            MaxRelayPort = settings.MaxRelayPort,
            MaxAllocations = settings.MaxAllocations,
            MaxAllocationsPerUser = settings.MaxAllocationsPerUser,
            AllowedPeers = IpRange.ParseList(settings.AllowedPeers),
        };
    }

    /// <summary>Return the credentials of the settings and the host's authenticators; throw when there are none, as nobody could use the server.</summary>
    private ITurnAuthenticator CreateAuthenticator(TurnOptions settings)
    {
        // Shared secrets, fixed users, then the host's
        List<ITurnAuthenticator> all = [];
        string[] secrets = (settings.SharedSecrets ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (secrets.Length > 0) all.Add(new SharedSecretAuthenticator(secrets, time));
        Dictionary<string, string> users = [];
        foreach (string user in (settings.Users ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = user.IndexOf(':');
            if (colon <= 0) throw new FormatException("Turn:Users needs name:password pairs separated by commas.");
            users[user[..colon]] = user[(colon + 1)..];
        }
        if (users.Count > 0) all.Add(new StaticUserAuthenticator(users));
        all.AddRange(authenticators);
        if (all.Count == 0) throw new InvalidOperationException("The TURN server has no credentials: set Turn:SharedSecrets or Turn:Users.");
        // Return them combined
        return all.Count == 1 ? all[0] : new CombinedAuthenticator(all);
    }

    /// <summary>Return an IP address of a setting; throw <see cref="FormatException"/> naming the setting.</summary>
    private static IPAddress ParseAddress(string text, string setting) => IPAddress.TryParse(text, out IPAddress? address) ? address : throw new FormatException($"{setting} \"{text}\" is not an IP address.");

    /// <summary>Return a fixed external address, or a source that finds it again at each check: DNS for a host name, a STUN server for <c>stun:host:port</c>; neither when not set.</summary>
    /// <param name="text">The setting.</param>
    /// <param name="local">Address STUN requests are sent from, as the route depends on it; any when 0.0.0.0.</param>
    private (IPAddress? Address, Func<CancellationToken, Task<IPAddress?>>? Source) ExternalAddress(string? text, IPAddress local)
    {
        // None, a fixed address, a STUN server, or a host name
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        text = text.Trim();
        if (IPAddress.TryParse(text, out IPAddress? address)) return (address, null);
        if (text.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
        {
            string server = text[5..];
            IPAddress? from = local.Equals(IPAddress.Any) ? null : local;
            return (null, async cancellationToken => await StunDiscovery.GetPublicAddressAsync(await ResolveAsync(server, 3478, cancellationToken), from, cancellationToken)
                ?? throw new InvalidOperationException($"The STUN server {server} of Turn:ExternalAddress did not answer."));
        }
        // Return a source resolving the name each time
        return (null, async cancellationToken => (await ResolveAsync(text, 0, cancellationToken)).Address);
    }

    /// <summary>Return the IPv4 address of <c>host</c> or <c>host:port</c>, with <paramref name="defaultPort"/> when no port is given; throw <see cref="InvalidOperationException"/> when it cannot be resolved.</summary>
    private static async Task<IPEndPoint> ResolveAsync(string text, int defaultPort, CancellationToken cancellationToken)
    {
        // Host and port, then DNS
        int colon = text.LastIndexOf(':');
        string host = colon > 0 ? text[..colon] : text;
        int port = colon > 0 && int.TryParse(text[(colon + 1)..], out int given) ? given : defaultPort;
        if (IPAddress.TryParse(host, out IPAddress? address)) return new IPEndPoint(address, port);
        IPAddress[] found;
        try
        {
            found = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Turn:ExternalAddress \"{text}\" cannot be resolved: {ex.Message}");
        }
        // Return the first IPv4 address
        return new IPEndPoint(found.FirstOrDefault() ?? throw new InvalidOperationException($"Turn:ExternalAddress \"{text}\" has no IPv4 address."), port);
    }
}
