namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

/// <summary>Users' <see href="https://spec.matrix.org/latest/client-server-api/#push-notifications">pushers</see>: where push notifications go, set by apps that want them, such as phone apps with a push gateway.</summary>
/// <remarks>A pusher belongs to the device that set it, and goes when that device logs out. <see cref="PushNotifier"/> sends to them.</remarks>
public sealed class PusherService
{
    /// <summary>Path that gateway URLs end with, as the spec requires.</summary>
    public const string GatewayPath = "/_matrix/push/v1/notify";
    private const int MaxPushKeyBytes = 512;
    private const int MaxAppIdLength = 64;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly TimeProvider time;

    /// <summary>Create the service.</summary>
    public PusherService(IDbContextFactory<ServerDbContext> contexts, TimeProvider time)
    {
        // Store dependencies
        this.contexts = contexts;
        this.time = time;
    }

    /// <summary>Return the user's pushers as the spec lists them.</summary>
    public async Task<JsonArray> GetAsync(string userId, CancellationToken cancellationToken)
    {
        // Rows in a stable order
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<PusherRow> rows = await db.Pushers.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.CreatedTs).ToListAsync(cancellationToken);
        // Return pushers
        return new JsonArray([.. rows.Select(ToJson)]);
    }

    /// <summary>Add, replace or, with a null <c>kind</c>, delete a pusher of the user, from the body of <c>POST /pushers/set</c>.</summary>
    /// <param name="userId">Owner.</param>
    /// <param name="deviceId">Device setting it, which owns it.</param>
    /// <param name="body">The pusher, with <c>pushkey</c>, <c>app_id</c> and <c>kind</c>, and when adding the display names, <c>lang</c> and <c>data</c>; <c>append</c> keeps pushers of other users with the same key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetAsync(string userId, string deviceId, JsonObject body, CancellationToken cancellationToken)
    {
        // Assert the key fields
        string pushKey = Required(body, "pushkey");
        string appId = Required(body, "app_id");
        if (System.Text.Encoding.UTF8.GetByteCount(pushKey) > MaxPushKeyBytes) throw Invalid($"pushkey is longer than {MaxPushKeyBytes} bytes.");
        if (appId.Length > MaxAppIdLength) throw Invalid($"app_id is longer than {MaxAppIdLength} characters.");
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        PusherRow? existing = await db.Pushers.SingleOrDefaultAsync(p => p.UserId == userId && p.AppId == appId && p.PushKey == pushKey, cancellationToken);

        // A null kind deletes
        if (!body.ContainsKey("kind") || body["kind"] is null)
        {
            if (existing is not null) db.Pushers.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Assert the rest of a pusher; http pushers need a gateway URL
        string kind = Required(body, "kind");
        if (kind is not ("http" or "email")) throw Invalid($"Unknown pusher kind {kind}.");
        if (body["data"] is not JsonObject data) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "The pusher needs data.");
        if (kind == "http" && !IsGatewayUrl(data["url"]?.GetValue<string>())) throw Invalid($"data.url must be an http or https URL ending in {GatewayPath}.");

        // Add or replace, then drop other users' pushers of the same key unless appending
        existing ??= db.Pushers.Add(new PusherRow { UserId = userId, AppId = appId, PushKey = pushKey, Kind = kind, AppDisplayName = "", DeviceDisplayName = "", Lang = "", DataJson = "{}" }).Entity;
        existing.Kind = kind;
        existing.AppDisplayName = Required(body, "app_display_name");
        existing.DeviceDisplayName = Required(body, "device_display_name");
        existing.ProfileTag = body["profile_tag"]?.GetValue<string>();
        existing.Lang = Required(body, "lang");
        existing.DataJson = data.ToJsonString();
        existing.DeviceId = deviceId;
        existing.CreatedTs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (body["append"]?.GetValue<bool>() != true) await db.Pushers.Where(p => p.AppId == appId && p.PushKey == pushKey && p.UserId != userId).ExecuteDeleteAsync(cancellationToken);
        // Done
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Delete pushers by key, e.g. those a gateway rejected.</summary>
    public async Task DeleteAsync(string userId, string appId, IReadOnlyCollection<string> pushKeys, CancellationToken cancellationToken)
    {
        // Rows of the user and app
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Pushers.Where(p => p.UserId == userId && p.AppId == appId && pushKeys.Contains(p.PushKey)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Return whether <paramref name="url"/> is an absolute http or https URL of the gateway path.</summary>
    public static bool IsGatewayUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath == GatewayPath;

    /// <summary>Return a pusher as JSON, as <c>GET /pushers</c> lists it.</summary>
    private static JsonObject ToJson(PusherRow row)
    {
        // Fields of the spec
        JsonObject pusher = new()
        {
            ["pushkey"] = row.PushKey,
            ["kind"] = row.Kind,
            ["app_id"] = row.AppId,
            ["app_display_name"] = row.AppDisplayName,
            ["device_display_name"] = row.DeviceDisplayName,
            ["lang"] = row.Lang,
            ["data"] = JsonNode.Parse(row.DataJson),
        };
        if (row.ProfileTag is not null) pusher["profile_tag"] = row.ProfileTag;
        // Return pusher
        return pusher;
    }

    /// <summary>Return a required string field; throw <c>M_MISSING_PARAM</c> when it is missing.</summary>
    private static string Required(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, $"The pusher needs {name}.");

    /// <summary>Return an <c>M_INVALID_PARAM</c> error.</summary>
    private static MatrixException Invalid(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, message);
}
