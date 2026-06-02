namespace Gert.Service.External;

/// <summary>
/// Port for the OpenAI-compatible chat-completion model (chat-and-tools.md
/// § tool loop). The real client (vLLM over <c>IHttpClientFactory</c> + Polly)
/// lives in <c>Gert.External</c> (U10); tests use a fake. Streaming yields
/// content deltas and tool-call requests; the orchestrator drives the loop.
/// </summary>
public interface IChatModelClient
{
    /// <summary>Stream a completion: text deltas interleaved with tool-call requests.</summary>
    IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A model-facing tool advertised in a completion request.</summary>
public sealed record ChatToolSpec
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON-schema of the tool's parameters (as a JSON string).</summary>
    public required string ParametersSchema { get; init; }
}

/// <summary>One message in the model conversation sent upstream.</summary>
public sealed record ChatModelMessage
{
    /// <summary>OpenAI-style role: <c>system</c> | <c>user</c> | <c>assistant</c> | <c>tool</c>.</summary>
    public required string Role { get; init; }

    public required string Content { get; init; }

    /// <summary>For tool-result messages: the id of the tool call this responds to.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>A request to the chat model — messages, model id, advertised tools, params.</summary>
public sealed record ChatCompletionRequest
{
    public required string ModelId { get; init; }

    public required IReadOnlyList<ChatModelMessage> Messages { get; init; }

    /// <summary>Tools offered this turn (already intersected to the entitlement ceiling).</summary>
    public IReadOnlyList<ChatToolSpec> Tools { get; init; } = [];

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? Seed { get; init; }
}

/// <summary>
/// One streamed chunk — either a text delta or a tool-call request. A null
/// <see cref="TextDelta"/> with a set <see cref="ToolCall"/> is a tool call; the
/// terminal chunk carries <see cref="FinishReason"/> and optionally a token count.
/// </summary>
public sealed record ChatModelChunk
{
    public string? TextDelta { get; init; }

    public ChatModelToolCall? ToolCall { get; init; }

    /// <summary>Set on the final chunk (e.g. <c>stop</c>, <c>tool_calls</c>, <c>length</c>).</summary>
    public string? FinishReason { get; init; }

    public int? TokenCount { get; init; }
}

/// <summary>A tool call requested by the model — the name and raw JSON arguments.</summary>
public sealed record ChatModelToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Tool arguments as a JSON string.</summary>
    public required string ArgumentsJson { get; init; }
}
