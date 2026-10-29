namespace Uniform.Matrix.Server.Http;

using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>HTTP conventions of the Matrix APIs: JSON with <see cref="MatrixJson.Options"/>, <see href="https://spec.matrix.org/latest/client-server-api/#standard-error-response">standard errors</see> and <see href="https://spec.matrix.org/latest/client-server-api/#web-browser-clients">CORS for browsers</see>.</summary>
public static class MatrixHttp
{
    /// <summary>Return a JSON result serialized like the rest of the protocol.</summary>
    public static IResult Json<T>(T value, int statusCode = StatusCodes.Status200OK) => Results.Json(value, MatrixJson.Options, statusCode: statusCode);

    /// <summary>Return a Matrix error result.</summary>
    public static IResult Error(HttpStatusCode status, string errcode, string? error) => Results.Json(new ErrorResponse { Errcode = errcode, Error = error }, MatrixJson.Options, statusCode: (int)status);

    /// <summary>Middleware: adds CORS headers, answers preflight requests, turns exceptions and bare 404 or 405 responses into Matrix errors.</summary>
    public static async Task HandleAsync(HttpContext context, RequestDelegate next, ILogger logger)
    {
        // Every response is readable by browser clients
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "X-Requested-With, Content-Type, Authorization";
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Errors of the request become standard error bodies
        try
        {
            await next(context);
        }
        catch (AuthenticationRequiredException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(ex.Response, MatrixJson.Options);
            return;
        }
        catch (MatrixErrorException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = (int)ex.StatusCode;
            JsonObject body = new() { ["errcode"] = ex.Errcode, ["error"] = ex.Message };
            foreach ((string key, JsonNode? value) in ex.Fields) body[key] = value?.DeepClone();
            await context.Response.WriteAsJsonAsync(body, MatrixJson.Options);
            return;
        }
        catch (MatrixException ex) when (!context.Response.HasStarted)
        {
            await WriteErrorAsync(context, ex.StatusCode, ex.Errcode, ex.Error);
            return;
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException && !context.Response.HasStarted)
        {
            // A missing or unreadable query parameter, else a body that is not JSON or not the expected JSON
            (string errcode, string message) = ParameterError(ex.Message) ?? (ex is JsonException || ex.InnerException is JsonException ? ErrorCodes.BadJson : ErrorCodes.NotJson, ex.Message);
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, errcode, message);
            return;
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Request {Method} {Path} failed", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, HttpStatusCode.InternalServerError, ErrorCodes.Unknown, "Internal server error.");
            return;
        }

        // Unknown endpoints and methods, which the router answers without a body; logged without the query, which may hold tokens, to show what clients miss
        if (context.Response.HasStarted || context.Response.ContentLength > 0 || context.Response.ContentType is not null) return;
        if (context.Response.StatusCode is not (StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)) return;
        logger.LogInformation("Unrecognized request {Method} {Path}", context.Request.Method, context.Request.Path);
        if (context.Response.StatusCode == StatusCodes.Status404NotFound) await WriteErrorAsync(context, HttpStatusCode.NotFound, ErrorCodes.Unrecognized, "Unrecognized request.");
        else await WriteErrorAsync(context, HttpStatusCode.MethodNotAllowed, ErrorCodes.Unrecognized, "Method not allowed.");
    }

    /// <summary>Write a Matrix error body.</summary>
    private static Task WriteErrorAsync(HttpContext context, HttpStatusCode status, string errcode, string? error)
    {
        // Status and JSON body
        context.Response.StatusCode = (int)status;
        // Done
        return context.Response.WriteAsJsonAsync(new ErrorResponse { Errcode = errcode, Error = error }, MatrixJson.Options);
    }

    /// <summary>Return the Matrix error of ASP.NET's message about a request parameter: <c>M_MISSING_PARAM</c> when a required one is missing, <c>M_INVALID_PARAM</c> when one cannot be read, with the parameter's name; null for other messages.</summary>
    private static (string Errcode, string Message)? ParameterError(string message)
    {
        // "Required parameter "string token" was not provided from query string."
        if (Regex.Match(message, @"^Required parameter ""[^""]*?(\w+)"" was not provided") is { Success: true } missing) return (ErrorCodes.MissingParam, $"Missing parameter {missing.Groups[1].Value}.");
        // "Failed to bind parameter "int limit" from "abc"."
        if (Regex.Match(message, @"^Failed to bind parameter ""[^""]*?(\w+)"" from") is { Success: true } invalid) return (ErrorCodes.InvalidParam, $"Invalid parameter {invalid.Groups[1].Value}.");
        // Return none
        return null;
    }
}

/// <summary>A Matrix error whose body has fields beyond <c>errcode</c> and <c>error</c>, e.g. <c>current_version</c> of <c>M_WRONG_ROOM_KEYS_VERSION</c>.</summary>
/// <param name="statusCode">HTTP status.</param>
/// <param name="errcode">Matrix error code.</param>
/// <param name="error">Human-readable message.</param>
/// <param name="fields">Extra fields of the body.</param>
public sealed class MatrixErrorException(HttpStatusCode statusCode, string errcode, string error, JsonObject fields) : Exception(error)
{
    /// <summary>HTTP status.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
    /// <summary>Matrix error code.</summary>
    public string Errcode { get; } = errcode;
    /// <summary>Extra fields of the body.</summary>
    public JsonObject Fields { get; } = fields;
}
