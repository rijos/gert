using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Gert.Api.Errors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Gert.Api.Security;

/// <summary>
/// Per-user rate limiting (security F10) using the built-in
/// <c>Microsoft.AspNetCore.RateLimiting</c>. Each authenticated caller gets its own
/// partition keyed by the token <c>(iss, sub)</c> pair (anonymous traffic falls back
/// to the remote IP), so one client - or one stolen token - can't saturate the box,
/// while one user's bursts never throttle another's. Limits are lenient (the deployment
/// is ~20 trusted users) and applied to the <c>/api/*</c> surface only; a rejected
/// request returns a branded <c>429</c> ProblemDetails. Limits bind from
/// <see cref="PolicyOptions"/> so tests can turn the cap down to prove the control.
/// </summary>
public static class RateLimiting
{
    /// <summary>The named policy applied to the API controllers.</summary>
    public const string PerUserPolicy = "per-user";

    /// <summary>
    /// Operator knobs for the per-user limiter (security F10), bound from
    /// <c>Gert:RateLimiting</c> (docs/installation/configuration.md). Defaults are the
    /// production posture - a generous 600-requests / 1-minute fixed window - so
    /// leaving the section absent changes nothing.
    /// </summary>
    public sealed class PolicyOptions
    {
        public const string SectionName = "Gert:RateLimiting";

        /// <summary>
        /// Max requests per partition (per user / per anonymous IP) within one
        /// window. Default: 600 - a DoS brake, not a usage quota.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int PermitLimit { get; set; } = 600;

        /// <summary>The fixed window length. Default: 1 minute.</summary>
        [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Register the per-user fixed-window limiter, applied via <see cref="PerUserPolicy"/>
    /// on the controller pipeline. Not added in the Testing environment (the caller
    /// guards that) so the suite isn't throttled.
    /// </summary>
    public static IServiceCollection AddGertRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Fail-fast options idiom (dotnet-style-guide section 4): a bad knob fails at startup.
        services.AddOptions<PolicyOptions>()
            .BindConfiguration(PolicyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

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
    /// Partition on the token <c>(iss, sub)</c> pair - the same identity anchor as the
    /// user folder key (principles.md: the user is <c>sha256(iss + sub)</c>, so a bare
    /// <c>sub</c> could collide across two IdPs) - falling back to the remote IP for
    /// anonymous traffic.
    /// </summary>
    private static RateLimitPartition<string> PartitionForRequest(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirstValue("sub");
        // '\n' separator: cannot appear in a JWT iss URL or sub, so "iss\nsub" is
        // collision-free without hashing on the hot path.
        var key = !string.IsNullOrEmpty(sub)
            ? $"user:{httpContext.User.FindFirstValue("iss")}\n{sub}"
            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        // IOptions resolve, not a captured snapshot: singleton options, trivial cost,
        // and the factory lambda below runs only once per new partition key anyway.
        var limits = httpContext.RequestServices
            .GetRequiredService<IOptions<PolicyOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = limits.Window,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    }
}
