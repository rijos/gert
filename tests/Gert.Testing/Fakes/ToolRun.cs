using Gert.Model.Chat;

namespace Gert.Testing.Fakes;

/// <summary>
/// A tool's model-facing <see cref="Gert.Tools.ToolResult"/> folded together with the side-effects it
/// pushed to the host card - the combined shape tool tests assert on now that the side-channels left
/// <see cref="Gert.Tools.ToolResult"/> (decisions #13). Produced by
/// <see cref="ToolExecutionExtensions.RunAsync"/>.
/// </summary>
public sealed record ToolRun(
    bool Success,
    string? ResultJson,
    string? Error,
    IReadOnlyList<Citation> Citations,
    string? Stdout,
    IReadOnlyList<TodoItem>? Todos,
    IReadOnlyList<Artifact>? Artifacts);
