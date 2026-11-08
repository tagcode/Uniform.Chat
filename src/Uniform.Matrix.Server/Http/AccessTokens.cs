namespace Uniform.Matrix.Server.Http;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>Access token authentication of endpoints: the token from the <c>Authorization</c> header, or the deprecated <c>access_token</c> query parameter.</summary>
public static class AccessTokens
{
    private const string RequesterKey = "Matrix.Requester";

    /// <summary>Require a valid access token; the endpoint reads the user with <see cref="GetRequester"/>.</summary>
    public static TBuilder RequireAccessToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder => builder.AddEndpointFilter(async (context, next) =>
    {
        // Token from header or query
        HttpContext http = context.HttpContext;
        string? header = http.Request.Headers.Authorization;
        string? token = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : http.Request.Query["access_token"];
        if (string.IsNullOrEmpty(token)) throw new MatrixException(HttpStatusCode.Unauthorized, ErrorCodes.MissingToken, "Missing access token.");

        // Known token
        AccountService accounts = http.RequestServices.GetRequiredService<AccountService>();
        Requester requester = await accounts.AuthenticateAsync(token, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted) ?? throw new MatrixException(HttpStatusCode.Unauthorized, ErrorCodes.UnknownToken, "Unknown access token.");
        if (requester.Locked && !IsLogout(http.Request.Path)) throw AccountService.LockedError();
        http.Items[RequesterKey] = requester;
        // Return endpoint result
        return await next(context);
    });

    /// <summary>Require an access token of a server admin; others get <c>M_FORBIDDEN</c>.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder => builder.RequireAccessToken().AddEndpointFilter(async (context, next) =>
    {
        // Admin flag of the account
        if (!context.HttpContext.GetRequester().Admin) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "You are not a server admin.");
        // Return endpoint result
        return await next(context);
    });

    /// <summary>Refuse suspended accounts with <c>M_USER_SUSPENDED</c>, for what <see href="https://spec.matrix.org/latest/client-server-api/#account-suspension">suspension</see> forbids: sending, joining, inviting, creating rooms, changing the profile and uploading.</summary>
    public static TBuilder RejectSuspended<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder => builder.AddEndpointFilter(async (context, next) =>
    {
        // Flag of the authenticated account
        if (context.HttpContext.Items[RequesterKey] is Requester { Suspended: true }) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.UserSuspended, "The account is suspended by an admin.");
        // Return endpoint result
        return await next(context);
    });

    /// <summary>Return whether <paramref name="path"/> logs out, which locked accounts may still do.</summary>
    private static bool IsLogout(PathString path) => path.Value is string value && (value.EndsWith("/logout", StringComparison.Ordinal) || value.EndsWith("/logout/all", StringComparison.Ordinal));

    /// <summary>Return the authenticated user of a request behind <see cref="RequireAccessToken"/>.</summary>
    public static Requester GetRequester(this HttpContext context) => context.Items[RequesterKey] as Requester ?? throw new InvalidOperationException("The endpoint does not require an access token.");

    /// <summary>Return the requester when it is <paramref name="userId"/>; throw <c>M_FORBIDDEN</c> for other users' data.</summary>
    public static Requester GetOwnRequester(this HttpContext context, string userId)
    {
        // Assert own user
        Requester requester = context.GetRequester();
        if (requester.UserId != userId) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Cannot use another user's data.");
        // Return requester
        return requester;
    }
}
