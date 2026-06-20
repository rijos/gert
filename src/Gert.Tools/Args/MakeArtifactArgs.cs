using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the canvas create tool (<c>make_artifact</c>): the file
/// <see cref="Name"/>, the model-facing <see cref="Format"/> word (validated for
/// membership against the canonical set), and the entire file <see cref="Content"/>.
/// </summary>
public sealed record MakeArtifactArgs
{
    /// <summary>File name with extension, e.g. <c>index.html</c> (required).</summary>
    [ToolParameterDescription("File name with extension, e.g. index.html or notes.md.")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The format word (html, markdown, svg, python, ...); required.</summary>
    [ToolParameterEnum("html", "markdown", "svg", "python", "csharp", "cpp", "javascript", "rust")]
    public string Format { get; init; } = string.Empty;

    /// <summary>The entire file content (required, non-empty).</summary>
    [ToolParameterDescription("The entire file content.")]
    public string Content { get; init; } = string.Empty;
}
