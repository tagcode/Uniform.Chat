namespace Uniform.Matrix.Server.Http;

using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;

/// <summary>Rate limit policies of the endpoints, from <see cref="RateLimitOptions"/>, answering with the Matrix <c>M_LIMIT_EXCEEDED</c> error.</summary>
public static class RateLimits
{
    /// <summary>Policy of writes, per access token.</summary>
    public const string Writes = "Uniform.Matrix.Server.Writes";
    /// <summary>Policy of logins and registrations, per client address.</summary>
    public const string Logins = "Uniform.Matrix.Server.Logins";

    /// <summary>Add the rate limiter with both policies.</summary>
    public static IServiceCollection AddMatrixRateLimits(this IServiceCollection services, RateLimitOptions options) => services.AddRateLimiter(limiter =>
    {
        // Matrix errors with the time to wait
        limiter.OnRejected = async (context, cancellationToken) =>
        {
            TimeSpan wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter) ? retryAfter : TimeSpan.FromSeconds(1);
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            await context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse { Errcode = ErrorCodes.LimitExceeded, Error = "Too many requests; wait and try again.", RetryAfterMs = (long)wait.TotalMilliseconds }, MatrixJson.Options, cancellationToken);
        };

        // Writes per token, logins per address; no limits when turned off
        limiter.AddPolicy(Writes, http => options.Enabled
            ? RateLimitPartition.GetTokenBucketLimiter(TokenOf(http), _ => Bucket(options.WriteBurst, options.WritesPerSecond, TimeSpan.FromSeconds(1)))
            : RateLimitPartition.GetNoLimiter(""));
        limiter.AddPolicy(Logins, http => options.Enabled
            ? RateLimitPartition.GetTokenBucketLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "", _ => Bucket(options.LoginBurst, options.LoginsPerMinute, TimeSpan.FromMinutes(1)))
            : RateLimitPartition.GetNoLimiter(""));
    });

    /// <summary>Return a token bucket of <paramref name="burst"/> tokens, refilled by <paramref name="rate"/> every <paramref name="period"/>; requests over it are refused at once, not queued.</summary>
    private static TokenBucketRateLimiterOptions Bucket(int burst, int rate, TimeSpan period) => new()
    {
        TokenLimit = Math.Max(1, burst),
        TokensPerPeriod = Math.Max(1, rate),
        ReplenishmentPeriod = period,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>Return the partition of a write: its access token, from the header or the query, or else the client address.</summary>
    private static string TokenOf(HttpContext http)
    {
        // Bearer header, then the deprecated query parameter
        string header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..];
        // Return query token or address
        return http.Request.Query["access_token"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "";
    }
}
