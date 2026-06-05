using Gert.Model.Chat;

namespace Gert.Service.Tools;

/// <summary>
/// A tool's outcome — what the orchestrator feeds back to the model and renders
/// as a <c>tool_result</c>. <see cref="ResultJson"/> is the opaque payload;
/// <see cref="Citations"/> seeds the message footnotes (RAG / web hits).
/// </summary>
public sealed record ToolResult
{
    public required bool Success { get; init; }

    /// <summary>Result payload as JSON (hits / results / stdout).</summary>
    public string? ResultJson { get; init; }

    /// <summary>Citations derived from this result, if any.</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
