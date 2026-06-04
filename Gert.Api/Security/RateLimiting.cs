using System.Security.Claims;
using System.Threading.RateLimiting;
using Gert.Api.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Gert.Api.Security;

/// <summary>
/// Per-user rate limiting (security F10) using the built-in
/// <c>Microsoft.AspNetCore.RateLimiting</c> — no extra package. Each authenticated
/// caller gets its own partition keyed by the token <c>sub</c> claim (anonymous
/// traffic falls back to the remote IP), so one client — or one stolen token —
/// can't saturate the box, while one user's bursts never throttle another's.
/// Limits are lenient (the deployment is ~20 trusted users), and the limiter is
/// applied to the <c>/api/*</c> surface only. A rejected request returns a branded
/// <c>429</c> ProblemDetails.
/// </summary>
public static class RateLimiting
{
    /// <summary>The named policy applied to the API controllers.</summary>
    public const string PerUserPolicy = "per-user";

    /// <summary>
    /// Register the per-user fixed-window limiter. Lenient by design; callers apply
    /// it via the <see cref="PerUserPolicy"/> on the controller pipeline. Not added in
    /// the Testing environment (the caller guards that) so the suite isn't throttled.
    /// </summary>
    public static IServiceCollection AddGertRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, _) =>
                await GertProblem.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "Too Many Requests",
                    "You have sent too many requests; please slow down.").ConfigureAwait(false);

            options.AddPolicy(PerUserPolicy, PartitionForRequest);
        });

        return services;
    }

    /// <summary>
    /// Partition on the token <c>sub</c> (the user folder anchor), falling back to the
    /// remote IP for anonymous traffic. A generous fixed window — the cap is a DoS
    /// brake, not a usage quota.
    /// </summary>
    private static RateLimitPartition<string> PartitionForRequest(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirstValue("sub");
        var key = !string.IsNullOrEmpty(sub)
            ? $"sub:{sub}"
            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    }
}
