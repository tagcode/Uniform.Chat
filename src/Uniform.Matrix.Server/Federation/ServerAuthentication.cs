namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;

/// <summary>Endpoint metadata of federation endpoints: they answer only with federation on, and when <see cref="Signed"/>, only requests signed by the server they come from.</summary>
/// <param name="Signed">Whether requests must carry a valid <c>X-Matrix</c> authorization.</param>
public sealed record FederationEndpoint(bool Signed);

/// <summary>Checks <see href="https://spec.matrix.org/latest/server-server-api/#request-authentication">signed requests</see> of other servers on endpoints with <see cref="FederationEndpoint"/> metadata, and tells endpoints who sent them, see <see cref="GetOrigin"/>.</summary>
/// <remarks>Runs as middleware rather than an endpoint filter, as the body must be read for the signature before the endpoint binds it.</remarks>
public static class ServerAuthentication
{
    private const string OriginKey = "Uniform.Matrix.Server.Origin";
    private const int MaxBodyBytes = 10 * 1024 * 1024;

    /// <summary>Mark endpoints as federation endpoints, signed by default.</summary>
    public static TBuilder RequireFederation<TBuilder>(this TBuilder builder, bool signed = true) where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new FederationEndpoint(signed));

    /// <summary>Return the server that sent a signed request.</summary>
    public static string GetOrigin(this HttpContext context) => context.Items[OriginKey] as string ?? throw new InvalidOperationException("The endpoint does not require a signed request.");

    /// <summary>Add the middleware; place it after routing and inside the error handling.</summary>
    public static IApplicationBuilder UseServerAuthentication(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Only federation endpoints, and only with federation on
        if (context.GetEndpoint()?.Metadata.GetMetadata<FederationEndpoint>() is not FederationEndpoint endpoint)
        {
            await next(context);
            return;
        }
        if (!context.RequestServices.GetRequiredService<IOptionsMonitor<FederationOptions>>().CurrentValue.Enabled) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.Unrecognized, "Federation is off on this server.");
        if (endpoint.Signed) context.Items[OriginKey] = await AuthenticateAsync(context);
        // Done
        await next(context);
    });

    /// <summary>Return the origin of a validly signed request; throw <c>M_UNAUTHORIZED</c> otherwise.</summary>
    private static async Task<string> AuthenticateAsync(HttpContext context)
    {
        // Assert an X-Matrix header from a valid server name
        List<XMatrixAuthorization> headers = [.. context.Request.Headers.Authorization.Select(FederationAuth.Parse).OfType<XMatrixAuthorization>()];
        if (headers.Count == 0) throw Unauthorized("Missing X-Matrix authorization.");
        string serverName = context.RequestServices.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue.ServerName;
        JsonNode? content = await ReadBodyAsync(context);
        string uri = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw ? raw : context.Request.GetEncodedPathAndQuery();

        // Any header valid with the origin's key, all from one origin
        RemoteServerKeys keys = context.RequestServices.GetRequiredService<RemoteServerKeys>();
        foreach (XMatrixAuthorization header in headers)
        {
            if (header.Origin != headers[0].Origin) throw Unauthorized("Authorization headers name different origins.");
            if (!ServerNames.TryParse(header.Origin, out _, out _)) throw Unauthorized("Invalid origin.");
            if (header.Destination is string destination && destination != serverName) throw Unauthorized($"The request is for {destination}, not this server.");
            if (await keys.GetVerifyKeyAsync(header.Origin, header.KeyId, context.RequestAborted) is not string publicKey) continue;
            if (FederationAuth.Verify(context.Request.Method, uri, header, serverName, content, publicKey)) return header.Origin;
        }
        // On error refuse
        throw Unauthorized($"The request is not validly signed by {headers[0].Origin}.");
    }

    /// <summary>Read the JSON body for the signature and leave it readable for the endpoint; null without a body.</summary>
    private static async Task<JsonNode?> ReadBodyAsync(HttpContext context)
    {
        // Bodiless requests
        if (context.Request.ContentLength is 0 || context.Request.ContentLength is null && !context.Request.Headers.TransferEncoding.Any()) return null;
        if (context.Request.ContentLength > MaxBodyBytes) throw new MatrixException(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.TooLarge, "Request is too large.");

        // Buffer, parse, rewind
        context.Request.EnableBuffering(MaxBodyBytes);
        JsonNode? content;
        try
        {
            content = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.NotJson, "Request body is not JSON.");
        }
        context.Request.Body.Position = 0;
        // Return content
        return content;
    }

    /// <summary>Return an <c>M_UNAUTHORIZED</c> error.</summary>
    private static MatrixException Unauthorized(string message) => new(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized, message);
}
