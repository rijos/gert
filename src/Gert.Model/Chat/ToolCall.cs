namespace Gert.Model.Chat;

/// <summary>
/// A tool invocation made while producing an assistant message - mirrors the
/// <c>tool_calls</c> row in a project's <c>chat.db</c> (storage-and-data.md
/// section chat.db). <c>request_json</c> / <c>response_json</c> are stored as opaque
/// JSON text (query/code in, hits/results/stdout out).
/// </summary>
public sealed record ToolCall
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string MessageId { get; init; }

    /// <summary>Capability id of the tool that ran (e.g. <c>rag</c>) - the <c>tool_calls.kind</c> column.</summary>
    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    /// <summary>Raw request payload as JSON (query / code).</summary>
    public string? RequestJson { get; init; }

    /// <summary>Raw response payload as JSON (hits / results / stdout).</summary>
    public string? ResponseJson { get; init; }

    /// <summary>Wall-clock latency, surfaced in the UI as e.g. "rag - 142ms".</summary>
    public long? LatencyMs { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
