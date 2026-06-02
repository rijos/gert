using System.Text;
using System.Text.Json;
using Gert.Model.Events;
using Microsoft.AspNetCore.Http;

namespace Gert.Api.Sse;

/// <summary>
/// Renders a stream of <see cref="ChatEvent"/>s as Server-Sent Events on an HTTP
/// response (rest-api.md § sending a message). Each event is written as a single
/// <c>event: &lt;EventName&gt;\ndata: &lt;json&gt;\n\n</c> frame and the response is
/// flushed per event so the client gets the typewriter effect in real time.
/// <para>
/// The <c>data:</c> payload is the polymorphic <see cref="ChatEvent"/> serialized
/// through System.Text.Json, so it carries the <c>type</c> discriminator and
/// round-trips back to the union on the client/test side.
/// </para>
/// </summary>
public static class SseWriter
{
    /// <summary>The SSE content type (with charset), set on the response before writing.</summary>
    public const string ContentType = "text/event-stream";

    /// <summary>JSON shape for SSE payloads — camelCase web defaults, matching the API surface.</summary>
    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Set the SSE response headers and stream every event from
    /// <paramref name="events"/> as a framed <c>event:</c>/<c>data:</c> block,
    /// flushing after each so deltas arrive incrementally.
    /// </summary>
    public static async Task WriteAsync(
        HttpResponse response,
        IAsyncEnumerable<ChatEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(events);

        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache";
        // SSE must not be buffered by an intermediary (rest-api.md § why SSE).
        response.Headers["X-Accel-Buffering"] = "no";

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var json = JsonSerializer.Serialize(evt, JsonOptions);
            var frame = new StringBuilder()
                .Append("event: ").Append(evt.EventName).Append('\n')
                .Append("data: ").Append(json).Append('\n')
                .Append('\n')
                .ToString();

            await response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
