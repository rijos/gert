namespace Gert.Tools;

/// <summary>
/// Arguments for the canvas create tool (<c>make_artifact</c>): the file
/// <see cref="Name"/>, the model-facing <see cref="Format"/> word (validated for
/// membership against the canonical set), and the entire file <see cref="Content"/>.
/// </summary>
public sealed record MakeArtifactArgs
{
    /// <summary>File name with extension, e.g. <c>index.html</c> (required).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The format word (html, markdown, svg, python, ...); required.</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>The entire file content (required, non-empty).</summary>
    public string Content { get; init; } = string.Empty;
}
