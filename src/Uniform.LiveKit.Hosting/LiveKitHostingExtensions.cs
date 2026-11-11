namespace Uniform.LiveKit.Hosting;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.LiveKit.Authorization;
using Uniform.LiveKit.Protocol;
using Uniform.Matrix.Hosting;

/// <summary>Adds the MatrixRTC authorization service, compatible with lk-jwt-service, to an ASP.NET host.</summary>
/// <example><code><![CDATA[
/// WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
/// builder.AddLiveKitAuthorization(); // serves when LiveKit:Enabled is true
/// WebApplication app = builder.Build();
/// app.MapLiveKitAuthorization();
/// await app.RunAsync();
/// ]]></code></example>
public static class LiveKitHostingExtensions
{
    /// <summary>Name of the HTTP client of OpenID verification and LiveKit's room service.</summary>
    public const string HttpClientName = "Uniform.LiveKit";

    /// <summary>Bind section <see cref="LiveKitOptions.Section"/>, list it among the settings, and register a <see cref="LiveKitAuthorizer"/>; register an <see cref="IOpenIdVerifier"/> before this to verify tokens otherwise than over federation.</summary>
    public static IHostApplicationBuilder AddLiveKitAuthorization(this IHostApplicationBuilder builder)
    {
        // Options and an HTTP client
        builder.Services.AddOptionSection<LiveKitOptions>(builder.Configuration, LiveKitOptions.Section);
        builder.Services.AddHttpClient(HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Federation verification unless replaced, then the authorizer reading the current options, checking room membership when a host registered it
        builder.Services.TryAddSingleton<IOpenIdVerifier>(services => new FederationOpenIdVerifier(services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        builder.Services.AddSingleton(services =>
        {
            IOptionsMonitor<LiveKitOptions> options = services.GetRequiredService<IOptionsMonitor<LiveKitOptions>>();
            HttpClient http = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return new LiveKitAuthorizer(() => options.CurrentValue.ToAuthorizer(), services.GetRequiredService<IOpenIdVerifier>(), http, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<LiveKitAuthorizer>>(), services.GetService<IRoomMembership>());
        });
        // Return builder
        return builder;
    }

    /// <summary>Map <c>/sfu/get</c>, <c>/get_token</c> and <c>/healthz</c> under <paramref name="prefix"/>, e.g. <c>/livekit/jwt</c>; they answer M_UNRECOGNIZED while <see cref="LiveKitOptions.Enabled"/> is false.</summary>
    public static IEndpointRouteBuilder MapLiveKitAuthorization(this IEndpointRouteBuilder routes, string prefix = "")
    {
        // The two token requests, with CORS for web clients
        RouteGroupBuilder group = routes.MapGroup(prefix);
        group.Map("/sfu/get", (HttpContext http, LiveKitAuthorizer authorizer, IOptionsMonitor<LiveKitOptions> options) => HandleAsync<LegacySfuRequest>(http, options, request => authorizer.AuthorizeLegacyAsync(request, http.RequestAborted)));
        group.Map("/get_token", (HttpContext http, LiveKitAuthorizer authorizer, IOptionsMonitor<LiveKitOptions> options) => HandleAsync<TokenRequest>(http, options, request => authorizer.AuthorizeAsync(request, http.RequestAborted)));

        // Health for load balancers and service managers
        group.MapMethods("/healthz", [HttpMethods.Get, HttpMethods.Head], (IOptionsMonitor<LiveKitOptions> options) => options.CurrentValue.Enabled ? Results.Ok() : Results.NotFound());
        // Return routes
        return routes;
    }

    /// <summary>Answer a token request: preflight with 200, other methods than POST with 405, and the body by <paramref name="authorize"/>.</summary>
    private static async Task<IResult> HandleAsync<T>(HttpContext http, IOptionsMonitor<LiveKitOptions> options, Func<T, Task<SfuResponse>> authorize) where T : class
    {
        // Assert enabled; CORS on every answer
        if (!options.CurrentValue.Enabled) return Error(new AuthorizationException(HttpStatusCode.NotFound, AuthorizationError.Unrecognized, "Unrecognized request"));
        http.Response.Headers.AccessControlAllowOrigin = "*";
        http.Response.Headers.AccessControlAllowMethods = "POST";
        http.Response.Headers.AccessControlAllowHeaders = "Accept, Content-Type, Content-Length, Accept-Encoding, X-CSRF-Token, Authorization";
        if (HttpMethods.IsOptions(http.Request.Method)) return Results.Ok();
        if (!HttpMethods.IsPost(http.Request.Method)) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        // The body, then the token
        T? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            return Error(new AuthorizationException(HttpStatusCode.BadRequest, AuthorizationError.BadJson, $"The request body is not valid: {e.Message}"));
        }
        if (request is null) return Error(new AuthorizationException(HttpStatusCode.BadRequest, AuthorizationError.BadJson, "The request body is empty."));
        try
        {
            return Results.Json(await authorize(request).ConfigureAwait(false));
        }
        catch (AuthorizationException e)
        {
            // On a refusal: its status and code
            return Error(e);
        }
    }

    /// <summary>Return <paramref name="error"/> as a Matrix error answer.</summary>
    private static IResult Error(AuthorizationException error) => Results.Json(error.ToError(), statusCode: (int)error.Status);
}
