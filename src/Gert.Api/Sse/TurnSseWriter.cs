using System.Text;
using System.Text.Json;
using Gert.Model.Events;
using Gert.Model.Json;

namespace Gert.Api.Sse;

/// <summary>
/// Renders a stream of <see cref="TurnEvent"/>s as Server-Sent Events
/// (rest-api.md section the stream endpoint). Each event is one frame:
/// <c>id: &lt;seq&gt;\nevent: &lt;type&gt;\ndata: &lt;chatEvent json&gt;\n\n</c>,
/// flushed per event for the live typewriter. The <c>id:</c> field carries the
/// seq cursor, so a reconnecting client resumes with <c>?after=&lt;lastId&gt;</c>
/// - the standard EventSource <c>Last-Event-ID</c> shape, even though our SPA
/// uses fetch.
/// </summary>
public static class TurnSseWriter
{
    /// <summary>The SSE content type, set on the response before writing.</summary>
    public const string ContentType = "text/event-stream";

    /// <summary>
    /// Set the SSE headers and stream every event as a framed
    /// <c>id:/event:/data:</c> block, flushing after each.
    /// </summary>
    public static async Task WriteAsync(
        HttpResponse response,
        IAsyncEnumerable<TurnEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(events);

        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache";
        // SSE must not be buffered by an intermediary (rest-api.md section why SSE).
        response.Headers["X-Accel-Buffering"] = "no";

        // Send the headers NOW: an idle conversation produces no event for a
        // while, and the client must see the stream open immediately.
        await response.StartAsync(cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var turnEvent in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var json = JsonSerializer.Serialize(turnEvent.Event, GertJsonOptions.Default);
            var frame = new StringBuilder()
                .Append("id: ").Append(turnEvent.Seq).Append('\n')
                .Append("event: ").Append(turnEvent.Event.Type.ToWireName()).Append('\n')
                .Append("data: ").Append(json).Append('\n')
                .Append('\n')
                .ToString();

            await response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
