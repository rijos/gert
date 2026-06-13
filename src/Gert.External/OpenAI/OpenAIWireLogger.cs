using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Gert.External.OpenAI;

/// <summary>
/// Debug-only wire tracer for the OpenAI-compatible upstream: logs the ACTUAL POST
/// body + headers Gert sends to vLLM (and the response status + headers) at
/// <c>Debug</c>, so an operator tuning sampling params, the tools block, or
/// <c>chat_template_kwargs</c> can see the exact request on the wire - including the
/// SDK-injected <c>model</c>/<c>stream</c>/<c>stream_options</c> fields the request
/// builder never sees. Registered innermost in the named-client handler chain, so it
/// traces each attempt as it leaves for the transport.
///
/// <para>
/// Entirely gated by the <c>Debug</c> level (one bool check otherwise), so it is silent
/// in a normal <c>Information</c> run. The <c>Authorization</c> bearer (the api key, F8)
/// is redacted - but the body is logged verbatim, which includes conversation CONTENT.
/// That is the single, deliberate exception to the never-log-content rule
/// (operations.md section logging format): a local tuning aid, never to be enabled at Debug in
/// production. Request bodies are buffered JSON (only the RESPONSE streams), so reading
/// the content here does not disturb the send.
/// </para>
/// </summary>
internal sealed class OpenAIWireLogger(ILogger<OpenAIWireLogger> logger) : DelegatingHandler
{
    /// <summary>Body log cap - generous (the whole tools block + params fit) but bounds a
    /// vision request's base64 image parts from ballooning a single log line.</summary>
    private const int MaxBodyChars = 100_000;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug(
            "OpenAI request: {Method} {Uri}\nheaders: {Headers}\nbody: {Body}",
            request.Method,
            request.RequestUri,
            FormatHeaders(request.Headers, request.Content?.Headers),
            Cap(body));

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // The response body is the SSE stream - left untouched so streaming is not
        // buffered; only the status + headers are traced.
        logger.LogDebug(
            "OpenAI response: {Status} {Uri}\nheaders: {Headers}",
            (int)response.StatusCode,
            request.RequestUri,
            FormatHeaders(response.Headers, response.Content?.Headers));

        return response;
    }

    private static string Cap(string body) =>
        body.Length <= MaxBodyChars
            ? body
            : $"{body[..MaxBodyChars]}...[+{body.Length - MaxBodyChars} chars]";

    /// <summary>
    /// Render header lines, redacting credential headers (F8). Content headers
    /// (Content-Type, Content-Length) fold in so the whole request shape is visible.
    /// </summary>
    private static string FormatHeaders(HttpHeaders headers, HttpHeaders? contentHeaders)
    {
        var all = contentHeaders is null ? headers : headers.Concat(contentHeaders);
        return string.Join(
            "; ",
            all.Select(h => IsSecret(h.Key)
                ? $"{h.Key}=<redacted>"
                : $"{h.Key}={string.Join(",", h.Value)}"));
    }

    private static bool IsSecret(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);
}
