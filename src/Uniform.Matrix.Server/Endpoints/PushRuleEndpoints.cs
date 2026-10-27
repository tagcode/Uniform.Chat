namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;

/// <summary>The requester's <see href="https://spec.matrix.org/latest/client-server-api/#push-rules-api">push rules</see>: read them, add and delete their own rules, and enable, disable or change any rule's actions.</summary>
public static class PushRuleEndpoints
{
    /// <summary>Map the push rule endpoints under <c>/_matrix/client/v3/pushrules</c>; only the <c>global</c> scope exists.</summary>
    public static IEndpointRouteBuilder MapPushRules(this IEndpointRouteBuilder routes)
    {
        // Whole rule set
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3/pushrules").RequireAccessToken();
        client.MapGet("/", async (HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["global"] = await rules.GetRulesetAsync(http.GetRequester().UserId, cancellationToken) }));
        client.MapGet("/{scope}/", async (string scope, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await rules.GetRulesetAsync(OwnScope(http, scope), cancellationToken)));

        // One rule
        client.MapGet("/{scope}/{kind}/{ruleId}", async (string scope, string kind, string ruleId, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await rules.GetRuleAsync(OwnScope(http, scope), kind, ruleId, cancellationToken)));
        client.MapPut("/{scope}/{kind}/{ruleId}", async (string scope, string kind, string ruleId, JsonObject body, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
        {
            string? before = http.Request.Query["before"].FirstOrDefault(), after = http.Request.Query["after"].FirstOrDefault();
            await rules.PutRuleAsync(OwnScope(http, scope), kind, ruleId, body, before, after, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapDelete("/{scope}/{kind}/{ruleId}", async (string scope, string kind, string ruleId, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
        {
            await rules.DeleteRuleAsync(OwnScope(http, scope), kind, ruleId, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Fields of a rule
        foreach (string field in new[] { "enabled", "actions" })
        {
            client.MapGet($"/{{scope}}/{{kind}}/{{ruleId}}/{field}", async (string scope, string kind, string ruleId, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
                MatrixHttp.Json(new JsonObject { [field] = (await rules.GetRuleAsync(OwnScope(http, scope), kind, ruleId, cancellationToken))[field]?.DeepClone() }));
            client.MapPut($"/{{scope}}/{{kind}}/{{ruleId}}/{field}", async (string scope, string kind, string ruleId, JsonObject body, HttpContext http, PushRuleService rules, CancellationToken cancellationToken) =>
            {
                JsonNode value = body[field] ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, $"The body needs {field}.");
                await rules.SetRuleFieldAsync(OwnScope(http, scope), kind, ruleId, field, value, cancellationToken);
                return MatrixHttp.Json(new JsonObject());
            });
        }
        // Return routes
        return routes;
    }

    /// <summary>Return the requester's user ID for the <c>global</c> scope; other scopes do not exist.</summary>
    private static string OwnScope(HttpContext http, string scope) =>
        scope == "global" ? http.GetRequester().UserId : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown push rule scope {scope}; only global exists.");
}
