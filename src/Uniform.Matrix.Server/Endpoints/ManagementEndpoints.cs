namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;

/// <summary>Calls of the local manager, <c>uniform-chat-server manage</c>, on the <see cref="AdminSocket"/> only: the server's status and files, and its settings, read, applied live and saved. Elsewhere they answer as unknown, so the network never sees them.</summary>
public static class ManagementEndpoints
{
    /// <summary>Map the manager's calls.</summary>
    public static IEndpointRouteBuilder MapManagement(this IEndpointRouteBuilder routes)
    {
        // The socket only
        RouteGroupBuilder manage = routes.MapGroup(AdminSocket.Prefix).AddEndpointFilter(async (context, next) =>
            context.HttpContext.IsAdminSocket() ? await next(context) : MatrixHttp.Error(HttpStatusCode.NotFound, ErrorCodes.Unrecognized, "Unrecognized request."));
        manage.MapGet("/status", Status);

        // Settings: values with their sources, then changes applied or saved
        manage.MapGet("/settings", (HttpContext http) => MatrixHttp.Json(Settings(http)));
        manage.MapPost("/settings/apply", (HttpContext http, JsonObject body) =>
        {
            (SettingsStore store, IReadOnlyDictionary<string, string?> changes) = Changes(http, body);
            store.Apply(changes);
            return MatrixHttp.Json(Settings(http));
        });
        manage.MapPost("/settings/save", (HttpContext http, JsonObject body) =>
        {
            (SettingsStore store, IReadOnlyDictionary<string, string?> changes) = Changes(http, body);
            try { store.Save(changes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MatrixException(HttpStatusCode.InternalServerError, ErrorCodes.Unknown, $"Cannot write {store.Path}: {ex.Message}"); }
            return MatrixHttp.Json(Settings(http));
        });
        // Return routes
        return routes;
    }

    /// <summary>Answer the status: name, version, addresses and counters, and the data, log and settings files.</summary>
    private static IResult Status(HttpContext http, ServerStatus status, IServer server, IOptions<ServerOptions> options, IOptions<ServerStorageOptions> storage, IHostEnvironment environment)
    {
        // Optional parts of the host: the session log and the settings file
        IServiceProvider services = http.RequestServices;
        IEnumerable<string> addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        // Return status
        return MatrixHttp.Json(new JsonObject
        {
            ["server_name"] = options.Value.ServerName,
            ["version"] = typeof(ManagementEndpoints).Assembly.GetName().Version?.ToString(3),
            ["addresses"] = new JsonArray([.. addresses.Select(a => (JsonNode)a)]),
            ["started_ts"] = status.Started.ToUnixTimeMilliseconds(),
            ["requests"] = status.Requests,
            ["active_requests"] = status.ActiveRequests,
            ["failed_requests"] = status.FailedRequests,
            ["data_directory"] = storage.Value.ResolveDataDirectory(environment.ApplicationName),
            ["log_directory"] = storage.Value.ResolveLogDirectory(environment.ApplicationName),
            ["log_file"] = services.GetService<FileLoggerProvider>()?.FilePath,
            ["settings_file"] = services.GetService<SettingsStore>()?.Path,
        });
    }

    /// <summary>Return the settings file and each option's value and source; a set secret shows as <c>(hidden)</c>, as secrets never leave the server.</summary>
    private static JsonObject Settings(HttpContext http)
    {
        // Every option of the server
        (SettingsStore store, IReadOnlyList<OptionInfo> options) = Catalog(http);
        JsonObject values = [];
        foreach (OptionInfo option in options)
        {
            string? value = store.GetValue(option.Key);
            values[option.Key] = new JsonObject { ["value"] = option.Secret && value is not null ? "(hidden)" : value, ["source"] = store.GetSource(option.Key) };
        }
        // Return settings
        return new JsonObject { ["settings_file"] = store.Path, ["values"] = values };
    }

    /// <summary>Return the store and the changes of a request: <c>changes</c>, each a known option that is not secret, to a valid value or null.</summary>
    private static (SettingsStore Store, IReadOnlyDictionary<string, string?> Changes) Changes(HttpContext http, JsonObject body)
    {
        // Known keys, valid values
        (SettingsStore store, IReadOnlyList<OptionInfo> options) = Catalog(http);
        if (body["changes"] is not JsonObject changes) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing changes.");
        Dictionary<string, string?> valid = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? node) in changes)
        {
            OptionInfo option = options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown option {key}.");
            if (option.Secret) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{key} is secret and cannot be changed here.");
            if (node is null)
            {
                valid[option.Key] = null;
                continue;
            }
            (string? value, string? error) = option.Parse(node.GetValue<string>());
            if (error is not null) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{key}: {error}");
            valid[option.Key] = value;
        }
        // Return changes
        return (store, valid);
    }

    /// <summary>Return the host's settings store and options; throw <c>M_NOT_FOUND</c> when the host has no settings, e.g. in tests.</summary>
    private static (SettingsStore Store, IReadOnlyList<OptionInfo> Options) Catalog(HttpContext http)
    {
        // Both, from the host
        IServiceProvider services = http.RequestServices;
        if (services.GetService<SettingsStore>() is not SettingsStore store || services.GetService<OptionCatalog>() is not OptionCatalog catalog) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "This server has no settings file.");
        // Return them
        return (store, catalog.GetAll(services.GetRequiredService<IConfiguration>()));
    }
}
