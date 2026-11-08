namespace Uniform.Chat.Server;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Server.Configuration;
using Uniform.Stun.Hosting;
using Uniform.Turn;

/// <summary>The STUN and TURN server inside the homeserver, when <c>Turn:Enabled</c> is set: calls get credentials for it, made with the homeserver's TURN secret or one of its own, and its addresses as their TURN URIs.</summary>
internal static class EmbeddedTurn
{
    /// <summary>Add the TURN server and connect it to the homeserver's calls; what is not set follows the homeserver's settings.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>Turn:Realm</c>: <c>Server:ServerName</c>.</description></item>
    /// <item><description><c>Turn:ExternalAddress</c>: the host of <c>Server:PublicBaseUrl</c>, else <c>Server:ServerName</c>, resolved through DNS.</description></item>
    /// <item><description><c>Turn:CertificatePath</c> and <c>Turn:KeyPath</c>: those of <c>Tls</c>.</description></item>
    /// <item><description><c>Calls:TurnSharedSecret</c>: a random secret of this run, which nobody else needs.</description></item>
    /// <item><description><c>Calls:TurnUris</c>: <c>turn:</c> over UDP and TCP and <c>turns:</c> over TLS at that host and the listening ports.</description></item>
    /// </list>
    /// </remarks>
    public static WebApplicationBuilder AddEmbeddedTurn(this WebApplicationBuilder builder)
    {
        // The server, accepting the calls' credentials
        builder.AddTurnServer();
        builder.Services.AddSingleton<ITurnAuthenticator, CallSecretAuthenticator>();

        // Defaults from the homeserver
        builder.Services.AddOptions<TurnOptions>().PostConfigure<IOptions<ServerOptions>, IOptions<TlsOptions>>((turn, server, tls) =>
        {
            turn.Realm ??= server.Value.ServerName;
            turn.ExternalAddress ??= PublicHost(server.Value);
            turn.CertificatePath ??= tls.Value.CertificatePath;
            turn.KeyPath ??= tls.Value.KeyPath;
        });

        // Calls use it, unless they name another TURN server
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddOptions<CallOptions>().PostConfigure<IOptions<TurnOptions>, IOptions<ServerOptions>>((calls, turn, server) =>
        {
            if (!turn.Value.Enabled) return;
            calls.TurnSharedSecret ??= secret;
            calls.TurnUris ??= string.Join(',', Uris(turn.Value, PublicHost(server.Value)));
        });
        // Return builder
        return builder;
    }

    /// <summary>Return the host clients reach the homeserver at: that of the public base URL, else the server name without a port.</summary>
    private static string PublicHost(ServerOptions server) => Uri.TryCreate(server.PublicBaseUrl, UriKind.Absolute, out Uri? url) ? url.Host : server.ServerName.Split(':')[0];

    /// <summary>Return the TURN URIs of the listeners at <paramref name="host"/>.</summary>
    private static IEnumerable<string> Uris(TurnOptions turn, string host)
    {
        // UDP, TCP, then TLS
        yield return $"turn:{host}:{turn.Port}?transport=udp";
        if (turn.Tcp) yield return $"turn:{host}:{turn.Port}?transport=tcp";
        if (turn.TlsPort is int tls) yield return $"turns:{host}:{tls}?transport=tcp";
    }

    /// <summary>Accepts the time-limited credentials that the homeserver's <c>/voip/turnServer</c> hands out, made with <c>Calls:TurnSharedSecret</c>.</summary>
    /// <param name="calls">The call settings, read at each request, so a changed secret applies at once.</param>
    /// <param name="time">Clock of the credentials' expiry.</param>
    private sealed class CallSecretAuthenticator(IOptionsMonitor<CallOptions> calls, TimeProvider time) : ITurnAuthenticator
    {
        /// <summary>Return the key the calls' secret gives the user, or none without a secret or after expiry.</summary>
        public IEnumerable<byte[]> GetKeys(string username, string realm) => calls.CurrentValue.TurnSharedSecret is string secret ? new SharedSecretAuthenticator([secret], time).GetKeys(username, realm) : [];
    }
}
