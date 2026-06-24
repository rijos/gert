namespace Gert.Tools;

/// <summary>
/// A tool's MODEL-FACING outcome - what the orchestrator feeds back to the model and renders as a
/// <c>tool_result</c>: just whether it succeeded, the opaque JSON payload the model reads, and a
/// model-correctable error. Everything else a call produces (footnote citations, canvas artifacts,
/// plain-text card output, the todo checklist) is "pushed into the tool" and emitted through the
/// <see cref="Hosting.IToolCard"/> seam on the host, not returned here (decisions #13).
/// </summary>
public sealed record ToolResult
{
    public required bool Success { get; init; }

    /// <summary>Result payload as JSON - what the model sees (a FAILED call may still carry one, e.g. the sandbox's exit_code/stderr).</summary>
    public string? ResultJson { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false (model-correctable).</summary>
    public string? Error { get; init; }
}
