namespace Uniform.Chat.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Uniform.LiveKit.Authorization;
using Uniform.LiveKit.Hosting;
using Uniform.LiveKit.Protocol;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;

/// <summary>The MatrixRTC authorization service inside the homeserver, at <see cref="Prefix"/>, when <c>LiveKit:Enabled</c> is set: it hands out tokens of a LiveKit server, verifying the homeserver's own OpenID tokens directly and those of other servers over federation.</summary>
internal static class EmbeddedLiveKit
{
    /// <summary>Path of the service, as reverse proxies in front of lk-jwt-service commonly have it.</summary>
    public const string Prefix = "/livekit/jwt";

    /// <summary>Add the service and connect it to the homeserver; what is not set follows the homeserver's settings.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>LiveKit:FullAccessHomeservers</c>: <c>Server:ServerName</c>, so that the homeserver's users start calls and others join them.</description></item>
    /// <item><description><c>Calls:LiveKitServiceUrl</c>: <see cref="Prefix"/> at <c>Server:PublicBaseUrl</c>, else at <c>https://</c> and <c>Server:ServerName</c>.</description></item>
    /// </list>
    /// </remarks>
    public static WebApplicationBuilder AddEmbeddedLiveKit(this WebApplicationBuilder builder)
    {
        // Own tokens without a round trip, then the service
        builder.Services.AddSingleton<IOpenIdVerifier>(services => new LocalOpenIdVerifier(services.GetRequiredService<OpenIdTokens>(), services.GetRequiredService<IOptionsMonitor<ServerOptions>>(), new FederationOpenIdVerifier(services.GetRequiredService<IHttpClientFactory>().CreateClient(LiveKitHostingExtensions.HttpClientName))));
        builder.AddLiveKitAuthorization();

        // Defaults from the homeserver
        builder.Services.AddOptions<LiveKitOptions>().PostConfigure<IOptions<ServerOptions>>((livekit, server) => livekit.FullAccessHomeservers ??= server.Value.ServerName);
        builder.Services.AddOptions<CallOptions>().PostConfigure<IOptionsMonitor<LiveKitOptions>, IOptions<ServerOptions>>((calls, livekit, server) =>
        {
            if (!livekit.CurrentValue.Enabled) return;
            calls.LiveKitServiceUrl ??= ServiceUrl(server.Value);
        });
        // Return builder
        return builder;
    }

    /// <summary>Serve the service at <see cref="Prefix"/>.</summary>
    public static WebApplication MapEmbeddedLiveKit(this WebApplication app)
    {
        // Done
        app.MapLiveKitAuthorization(Prefix);
        return app;
    }

    /// <summary>Return the service's URL as clients reach the homeserver.</summary>
    public static string ServiceUrl(ServerOptions server) => $"{(string.IsNullOrWhiteSpace(server.PublicBaseUrl) ? $"https://{server.ServerName}" : server.PublicBaseUrl.TrimEnd('/'))}{Prefix}";

    /// <summary>Verifies tokens this homeserver issued in its own store, and others over federation.</summary>
    /// <param name="tokens">The homeserver's OpenID tokens.</param>
    /// <param name="server">The homeserver's settings, for its name.</param>
    /// <param name="federation">Verifies tokens of other servers.</param>
    private sealed class LocalOpenIdVerifier(OpenIdTokens tokens, IOptionsMonitor<ServerOptions> server, IOpenIdVerifier federation) : IOpenIdVerifier
    {
        /// <summary>Return the user of the token, from the own store when this homeserver issued it.</summary>
        public Task<string?> VerifyAsync(OpenIdToken token, CancellationToken cancellationToken) => string.Equals(token.MatrixServerName, server.CurrentValue.ServerName, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(tokens.Validate(token.AccessToken))
            : federation.VerifyAsync(token, cancellationToken);
    }
}
