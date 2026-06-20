using Gert.Model.Chat;

namespace Gert.Tools;

/// <summary>
/// The typed outcome of an <see cref="IToolCall{TArgs, TResult}"/>: the strongly-typed
/// <see cref="Value"/> the base serializes into <see cref="ToolResult.ResultJson"/>, plus the
/// same display/footnote side-channels a raw <see cref="ToolResult"/> carries. A failure carries
/// an <see cref="Error"/> the model can correct from, never a typed value.
/// </summary>
/// <typeparam name="TResult">The tool's result payload type.</typeparam>
public sealed record ToolCallResult<TResult>
{
    public required bool Success { get; init; }

    /// <summary>The typed result; serialized to <see cref="ToolResult.ResultJson"/> on success.</summary>
    public TResult? Value { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false (model-correctable).</summary>
    public string? Error { get; init; }

    /// <summary>Citations derived from this result, if any (RAG / web hits).</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Plain-text display output for the tool card (presentation only).</summary>
    public string? Stdout { get; init; }

    /// <summary>The todo list for the todo card (the <c>set_todos</c> tool).</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }

    /// <summary>Artifacts this call created or updated (the make/edit canvas tools).</summary>
    public IReadOnlyList<Artifact>? Artifacts { get; init; }

    /// <summary>A successful result carrying <paramref name="value"/> and optional side-channels.</summary>
    public static ToolCallResult<TResult> Ok(
        TResult value,
        IReadOnlyList<Citation>? citations = null,
        string? stdout = null,
        IReadOnlyList<TodoItem>? todos = null,
        IReadOnlyList<Artifact>? artifacts = null) =>
        new()
        {
            Success = true,
            Value = value,
            Citations = citations ?? [],
            Stdout = stdout,
            Todos = todos,
            Artifacts = artifacts,
        };

    /// <summary>A model-correctable failure carrying <paramref name="error"/>.</summary>
    public static ToolCallResult<TResult> Fail(string error) =>
        new() { Success = false, Error = error };
}
