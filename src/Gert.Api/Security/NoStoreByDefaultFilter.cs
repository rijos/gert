using Microsoft.AspNetCore.Mvc.Filters;

namespace Gert.Api.Security;

/// <summary>
/// Fail-closed cache control: every controller response carries
/// <c>Cache-Control: no-store</c> unless the action already set one. Gert serves per-user
/// data from a token-scoped store (principles.md #1); an intermediary cache - the Caddy
/// edge, a future CDN, a corporate proxy - that heuristically cached an authenticated
/// response could hand one user's data to another. Stamping no-store by default closes that
/// by construction. An endpoint that genuinely wants caching sets its own Cache-Control
/// first and this filter leaves it untouched (the SSE stream's own <c>no-cache</c> is one
/// such case). Scoped to the MVC pipeline (registered as a global filter), so static assets
/// (MapStaticAssets) and the anonymous health probes are never touched.
/// </summary>
public sealed class NoStoreByDefaultFilter : IResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.HttpContext.Response;

        // HasStarted guards a streaming action that already flushed headers (it would
        // throw on a header write); ContainsKey yields to an endpoint's deliberate policy.
        if (!response.HasStarted && !response.Headers.ContainsKey("Cache-Control"))
        {
            response.Headers.CacheControl = "no-store";
        }
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
        // Headers are committed once the result executes; nothing to do here.
    }
}
