namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>A user's <see href="https://spec.matrix.org/latest/client-server-api/#client-config">account data</see>, global and per room, including <see href="https://spec.matrix.org/latest/client-server-api/#room-tagging">room tags</see>.</summary>
/// <remarks>Each change takes a <see cref="ServerStream"/> position, so incremental syncs return it.</remarks>
public sealed class AccountDataService
{
    /// <summary>Room account data type of room tags.</summary>
    public const string TagType = "m.tag";
    /// <summary>Room account data type of the fully read marker.</summary>
    public const string FullyReadType = "m.fully_read";
    private static readonly string[] ServerControlledTypes = [FullyReadType, "m.push_rules"];
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;

    /// <summary>Create the service.</summary>
    public AccountDataService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
    }

    /// <summary>Set account data from a client; <paramref name="roomId"/> is null for global data. Throws <c>M_BAD_JSON</c> for types the server controls.</summary>
    public Task SetFromClientAsync(string userId, string? roomId, string type, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert client-settable type and a room ID
        if (ServerControlledTypes.Contains(type)) throw new MatrixException(HttpStatusCode.MethodNotAllowed, "M_BAD_JSON", $"{type} is controlled by the server; use its own API.");
        if (roomId is not null && !roomId.StartsWith('!')) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{roomId} is not a room ID.");
        // Done
        return SetAsync(userId, roomId, type, content, cancellationToken);
    }

    /// <summary>Set account data; <paramref name="roomId"/> is null for global data.</summary>
    public Task SetAsync(string userId, string? roomId, string type, JsonObject content, CancellationToken cancellationToken) =>
        stream.WriteAsync(async position =>
        {
            // Insert or replace at this position
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            string room = roomId ?? "";
            AccountDataRow? row = await db.AccountData.SingleOrDefaultAsync(a => a.UserId == userId && a.RoomId == room && a.Type == type, cancellationToken);
            if (row is null) db.AccountData.Add(row = new AccountDataRow { UserId = userId, RoomId = room, Type = type, Json = "" });
            row.Json = content.ToJsonString();
            row.Stream = position;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Return account data content, or null when not set; <paramref name="roomId"/> is null for global data.</summary>
    public async Task<JsonObject?> TryGetAsync(string userId, string? roomId, string type, CancellationToken cancellationToken)
    {
        // Stored JSON
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string room = roomId ?? "";
        string? json = await db.AccountData.AsNoTracking().Where(a => a.UserId == userId && a.RoomId == room && a.Type == type).Select(a => a.Json).SingleOrDefaultAsync(cancellationToken);
        // Return content
        return json is null ? null : JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>Return account data content; throw <c>M_NOT_FOUND</c> when not set.</summary>
    public async Task<JsonObject> GetAsync(string userId, string? roomId, string type, CancellationToken cancellationToken) =>
        await TryGetAsync(userId, roomId, type, cancellationToken) ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Account data not found.");

    /// <summary>Return the room's tags, e.g. <c>{"m.favourite":{"order":0.5}}</c>.</summary>
    public async Task<JsonObject> GetTagsAsync(string userId, string roomId, CancellationToken cancellationToken)
    {
        // Tags object of m.tag
        JsonObject? content = await TryGetAsync(userId, roomId, TagType, cancellationToken);
        // Return tags
        return content?["tags"]?.DeepClone() as JsonObject ?? [];
    }

    /// <summary>Add or replace a tag of a room; <paramref name="info"/> holds its optional <c>order</c>.</summary>
    public async Task SetTagAsync(string userId, string roomId, string tag, JsonObject info, CancellationToken cancellationToken)
    {
        // Assert tag length and order type
        if (tag.Length > 255) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Tags are at most 255 characters.");
        if (info["order"] is JsonNode order && order.GetValueKind() != System.Text.Json.JsonValueKind.Number) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "order must be a number.");

        // Replace the tag in m.tag
        JsonObject tags = await GetTagsAsync(userId, roomId, cancellationToken);
        tags[tag] = info.DeepClone();
        await SetAsync(userId, roomId, TagType, new JsonObject { ["tags"] = tags }, cancellationToken);
    }

    /// <summary>Remove a tag of a room; removing a missing tag does nothing.</summary>
    public async Task DeleteTagAsync(string userId, string roomId, string tag, CancellationToken cancellationToken)
    {
        // Rewrite m.tag without it
        JsonObject tags = await GetTagsAsync(userId, roomId, cancellationToken);
        if (!tags.Remove(tag)) return;
        await SetAsync(userId, roomId, TagType, new JsonObject { ["tags"] = tags }, cancellationToken);
    }
}
