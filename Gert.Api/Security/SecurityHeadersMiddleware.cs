using Microsoft.Extensions.Options;

namespace Gert.Api.Security;

/// <summary>
/// Emits the strict security headers (security F1; operations.md § HTTP security
/// headers &amp; CSP) on every <b>HTML</b> response — the SPA shell and any
/// client-route fallback that renders LLM/user-authored content while a bearer
/// token lives in the browser. The CSP is the single highest-value control: it
/// needs no <c>unsafe-inline</c> (the no-bundle ESM design uses external
/// <c>&lt;script type="module" src&gt;</c>), and <c>connect-src</c> lists only
/// <c>'self'</c> + the configured Pocket ID origin so a stolen token has nowhere to
/// be sent. The artifact iframe's own CSP is the SPA's concern (U12), not here.
/// <para>
/// Headers are stamped just before the body is written (an <c>OnStarting</c>
/// callback) and only when the negotiated content type is HTML, so JSON/SSE/file
/// responses are untouched.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _contentSecurityPolicy;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        ArgumentNullException.ThrowIfNull(options);
        _contentSecurityPolicy = BuildCsp(options.Value.PocketIdOrigin);
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var (ctx, csp) = ((HttpContext, string))state;
            var contentType = ctx.Response.ContentType;

            // Only HTML responses carry the CSP + frame controls. The browser applies
            // CSP to documents it renders; JSON/SSE/downloads don't need it.
            if (contentType is not null &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var headers = ctx.Response.Headers;
                headers["Content-Security-Policy"] = csp;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Frame-Options"] = "DENY";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            }

            return Task.CompletedTask;
        },
        (context, _contentSecurityPolicy));

        return _next(context);
    }

    /// <summary>
    /// Build the CSP, splicing the Pocket ID origin into <c>connect-src</c> (and
    /// nothing else). When no origin is configured, <c>connect-src</c> is just
    /// <c>'self'</c>.
    /// </summary>
    private static string BuildCsp(string pocketIdOrigin)
    {
        var connect = string.IsNullOrWhiteSpace(pocketIdOrigin)
            ? "'self'"
            : $"'self' {pocketIdOrigin}";

        return string.Join("; ",
        [
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self'",
            "img-src 'self' data:",
            $"connect-src {connect}",
            "frame-src 'self'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'self'",
            "frame-ancestors 'none'",
        ]);
    }
}
