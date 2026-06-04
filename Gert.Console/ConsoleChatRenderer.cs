using System.Globalization;
using Gert.Model.Events;

namespace Gert.Console;

/// <summary>
/// Renders a chat <see cref="ChatEvent"/> stream to text writers — the Console's
/// analog of the Api's SSE renderer (tech-stack.md § Architecture: the service
/// yields <c>IAsyncEnumerable&lt;ChatEvent&gt;</c>, the Api frames it as SSE and
/// the Console prints it; transport never leaks into the service).
/// <para>
/// The mapping:
/// <list type="bullet">
///   <item><c>message_start</c> → nothing (the assistant text follows inline).</item>
///   <item><c>delta</c> → the token text, written inline (the typewriter effect).</item>
///   <item><c>tool_call</c> → a <c>» tool: &lt;kind&gt; …</c> line.</item>
///   <item><c>tool_result</c> → a <c>✓ &lt;kind&gt; (&lt;n&gt; hits)</c> line.</item>
///   <item><c>citation</c> → a <c>[n] label</c> line.</item>
///   <item><c>artifact</c> → a <c>⛁ artifact: &lt;name&gt;</c> line.</item>
///   <item><c>message_end</c> → a trailing newline + the final token count.</item>
///   <item><c>error</c> → the message on the <b>error</b> writer (stderr).</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConsoleChatRenderer
{
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    /// <summary>Render to the given writers (stdout for the stream, stderr for errors).</summary>
    public ConsoleChatRenderer(TextWriter output, TextWriter error)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>
    /// Drain <paramref name="events"/>, writing each event's rendering. Returns once
    /// the stream completes.
    /// </summary>
    public async Task RenderAsync(
        IAsyncEnumerable<ChatEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            Render(evt);
        }
    }

    /// <summary>Render a single event (the synchronous core, exposed for testing one frame).</summary>
    public void Render(ChatEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt)
        {
            case MessageStartEvent:
                // The bubble marker has no stdout analog — the assistant text
                // streams in via the deltas that follow.
                break;

            case DeltaEvent delta:
                _out.Write(delta.Text);
                break;

            case ToolCallEvent call:
                _out.WriteLine();
                _out.WriteLine($"» tool: {call.Kind} …");
                break;

            case ToolResultEvent result:
                _out.WriteLine($"✓ {result.Kind} ({result.Hits?.Count ?? 0} hits)");
                break;

            case CitationEvent citation:
                _out.WriteLine($"[{citation.Ordinal.ToString(CultureInfo.InvariantCulture)}] {citation.Label}");
                break;

            case ArtifactEvent artifact:
                _out.WriteLine($"⛁ artifact: {artifact.Name}");
                break;

            case MessageEndEvent end:
                _out.WriteLine();
                _out.WriteLine(
                    end.TokenCount is { } tokens
                        ? $"[{tokens.ToString(CultureInfo.InvariantCulture)} tokens]"
                        : "[done]");
                break;

            case ErrorEvent error:
                _error.WriteLine($"error: {error.Message}");
                break;

            default:
                // Unknown future event types are ignored rather than crashing the host.
                break;
        }
    }
}
