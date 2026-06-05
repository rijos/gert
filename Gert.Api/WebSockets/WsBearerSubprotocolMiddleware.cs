namespace Gert.Api.WebSockets;

/// <summary>
/// Lifts the WS bearer subprotocol into the <c>Authorization</c> header BEFORE
/// authentication runs (security F2). Browsers cannot set an Authorization
/// header on a WebSocket and the SPA keeps the token in memory only — never in
/// the URL — so the client offers <c>["bearer", &lt;token&gt;]</c> as
/// subprotocols. Rewriting here (rather than authenticating inside the
/// endpoint) matters: the authentication handler caches its result per request,
/// so the header must exist before <c>UseAuthentication</c> — and it means WS
/// endpoints get the SAME JwtBearer validation + fallback authorization policy
/// as every other endpoint, with no AllowAnonymous holes.
/// </summary>
public sealed class WsBearerSubprotocolMiddleware
{
    private readonly RequestDelegate _next;

    public WsBearerSubprotocolMiddleware(RequestDelegate next) =>
        _next = next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest
            && string.IsNullOrEmpty(context.Request.Headers.Authorization))
        {
            // Entries arrive as "bearer, <token>"; hosts differ on trimming.
            var protocols = context.Request.Headers.SecWebSocketProtocol.ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (protocols.Length >= 2
                && string.Equals(protocols[0], ChatWebSocketEndpoint.BearerProtocol, StringComparison.Ordinal))
            {
                context.Request.Headers.Authorization = $"Bearer {protocols[1]}";
            }
        }

        return _next(context);
    }
}
