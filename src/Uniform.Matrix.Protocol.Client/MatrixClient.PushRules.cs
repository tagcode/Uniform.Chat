namespace Uniform.Matrix.Protocol.Client;

using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

// Push rules, which decide what notifies, and pushers, which say where notifications go.
public sealed partial class MatrixClient
{
    /// <summary>Return the user's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3pushrules">push rules</see> as JSON by kind: <c>override</c>, <c>content</c>, <c>room</c>, <c>sender</c> and <c>underride</c>.</summary>
    public Task<JsonElement> GetPushRulesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, "_matrix/client/v3/pushrules/global/", body: null, authenticated: true, cancellationToken);

    /// <summary>Add or replace a <see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3pushrulesglobalkindruleid">push rule</see> of the user.</summary>
    /// <param name="kind">Rule kind, e.g. <c>room</c> to change a room's notifications, with the room ID as <paramref name="ruleId"/>.</param>
    /// <param name="ruleId">Rule ID; a room ID for <c>room</c> rules, a user ID for <c>sender</c> rules.</param>
    /// <param name="rule">The rule's <c>actions</c>, with <c>conditions</c> or <c>pattern</c> as its kind needs, e.g. <c>new { actions = Array.Empty&lt;object&gt;() }</c> to mute.</param>
    /// <param name="before">Rule ID to place it before, or null.</param>
    /// <param name="after">Rule ID to place it after, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetPushRuleAsync(string kind, string ruleId, object rule, string? before = null, string? after = null, CancellationToken cancellationToken = default)
    {
        // Placement in the query
        string query = before is not null ? $"?before={Escape(before)}" : after is not null ? $"?after={Escape(after)}" : "";
        // Done
        return SendAsync(HttpMethod.Put, $"_matrix/client/v3/pushrules/global/{Escape(kind)}/{Escape(ruleId)}{query}", rule, authenticated: true, cancellationToken);
    }

    /// <summary>Delete one of the user's own push rules.</summary>
    public Task DeletePushRuleAsync(string kind, string ruleId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_matrix/client/v3/pushrules/global/{Escape(kind)}/{Escape(ruleId)}", body: null, authenticated: true, cancellationToken);

    /// <summary>Enable or disable a push rule, including the server's default rules.</summary>
    public Task SetPushRuleEnabledAsync(string kind, string ruleId, bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/pushrules/global/{Escape(kind)}/{Escape(ruleId)}/enabled", new { enabled }, authenticated: true, cancellationToken);

    /// <summary>Replace the actions of a push rule, e.g. <c>["notify"]</c>.</summary>
    public Task SetPushRuleActionsAsync(string kind, string ruleId, IReadOnlyList<object> actions, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/pushrules/global/{Escape(kind)}/{Escape(ruleId)}/actions", new { actions }, authenticated: true, cancellationToken);

    /// <summary>Return the user's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3pushers">pushers</see> as JSON: an array of pushers with their app, key and data.</summary>
    public async Task<JsonElement> GetPushersAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<JsonElement>(HttpMethod.Get, "_matrix/client/v3/pushers", body: null, authenticated: true, cancellationToken)).GetProperty("pushers");

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3pushersset">Add, replace or delete a pusher</see>: an object with <c>pushkey</c>, <c>app_id</c> and <c>kind</c> (null deletes), and when adding <c>app_display_name</c>, <c>device_display_name</c>, <c>lang</c> and <c>data</c> with the gateway <c>url</c>.</summary>
    public Task SetPusherAsync(object pusher, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "_matrix/client/v3/pushers/set", pusher, authenticated: true, cancellationToken);
}
