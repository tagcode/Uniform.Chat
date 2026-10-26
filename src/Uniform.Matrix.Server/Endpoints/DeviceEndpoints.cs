namespace Uniform.Matrix.Server.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#device-management">Device management</see> of the requester's own devices.</summary>
public static class DeviceEndpoints
{
    /// <summary>Map the device endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapDevices(this IEndpointRouteBuilder routes)
    {
        // List, read, rename
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapGet("/devices", async (HttpContext http, AccountService accounts, CancellationToken cancellationToken) => MatrixHttp.Json(new DevicesResponse { Devices = await accounts.GetDevicesAsync(http.GetRequester().UserId, cancellationToken) }));
        client.MapGet("/devices/{deviceId}", async (string deviceId, HttpContext http, AccountService accounts, CancellationToken cancellationToken) => MatrixHttp.Json(await accounts.GetDeviceAsync(http.GetRequester().UserId, deviceId, cancellationToken)));
        client.MapPut("/devices/{deviceId}", async (string deviceId, JsonObject body, HttpContext http, AccountService accounts, CancellationToken cancellationToken) =>
        {
            await accounts.RenameDeviceAsync(http.GetRequester().UserId, deviceId, body["display_name"]?.GetValue<string>(), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Delete one or several, after the password
        client.MapDelete("/devices/{deviceId}", async (string deviceId, HttpContext http, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken) =>
            await DeleteAsync(http, await ReadOptionalBodyAsync(http, cancellationToken), [deviceId], accounts, auth, cancellationToken));
        client.MapPost("/delete_devices", async (JsonObject body, HttpContext http, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken) =>
            await DeleteAsync(http, body, [.. (body["devices"] as JsonArray ?? []).Select(d => d!.GetValue<string>())], accounts, auth, cancellationToken));
        // Return routes
        return routes;
    }

    /// <summary>Delete devices after password auth.</summary>
    private static async Task<IResult> DeleteAsync(HttpContext http, JsonObject? body, IReadOnlyCollection<string> deviceIds, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken)
    {
        // Password of the account, then delete
        Requester requester = http.GetRequester();
        await AccountEndpoints.RequirePasswordAsync(requester, body?["auth"] as JsonObject, accounts, auth, cancellationToken);
        await accounts.DeleteDevicesAsync(requester.UserId, deviceIds, cancellationToken);
        // Return empty
        return MatrixHttp.Json(new JsonObject());
    }

    /// <summary>Read a JSON body that may be missing, as for DELETE requests.</summary>
    private static async Task<JsonObject?> ReadOptionalBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // Empty body means no auth yet
        if (http.Request.ContentLength is 0 or null && !http.Request.Headers.ContentType.Any()) return null;
        // Return parsed object
        return await http.Request.ReadFromJsonAsync<JsonObject>(MatrixJson.Options, cancellationToken);
    }
}
