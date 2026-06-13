using Gert.Model.Chat;

namespace Gert.Service.Tools;

/// <summary>
/// A tool's outcome - what the orchestrator feeds back to the model and renders
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

    /// <summary>
    /// Plain-text display output for the tool card (sandbox stdout, the clock
    /// reading) - presentation only; the model sees <see cref="ResultJson"/>.
    /// </summary>
    public string? Stdout { get; init; }

    /// <summary>The todo list for the todo card (the <c>set_todos</c> tool).</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }

    /// <summary>
    /// Artifacts this call created or updated (the make/edit canvas tools). The
    /// orchestrator persists nothing here - the tool already did - it only emits
    /// one <c>ArtifactEvent</c> per entry so the live canvas opens/updates. An
    /// entry re-using an existing artifact <c>Id</c> updates that tab in place.
    /// </summary>
    public IReadOnlyList<Artifact>? Artifacts { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
