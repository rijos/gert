using Gert.Model.Events;
using Gert.Service.External;

namespace Gert.Service.Chat;

/// <summary>
/// The buffered result of one model streaming call inside the tool loop — the
/// text deltas, any tool-call requests, the final token count, and a captured
/// model error (if the stream faulted). Buffering lets <see cref="ChatService"/>
/// drain the call inside a try/catch and then yield, since C# forbids a yield
/// inside a try.
/// </summary>
internal sealed record ModelStep
{
    public required IReadOnlyList<string> Deltas { get; init; }

    public required IReadOnlyList<ChatModelToolCall> ToolCalls { get; init; }

    public int? TokenCount { get; init; }

    /// <summary>Non-null when the model stream faulted — surfaced as the terminal error.</summary>
    public ErrorEvent? Error { get; init; }
}
