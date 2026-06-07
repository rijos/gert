using Gert.Model;

namespace Gert.Service.Tools;

/// <summary>
/// Shared format ↔ <see cref="ArtifactKind"/> mapping for the canvas artifact
/// tools (make/edit/read). The model-facing <c>format</c> is the human word
/// (<c>python</c>, <c>markdown</c>); common short aliases (<c>py</c>, <c>md</c>)
/// are accepted too so a near-miss doesn't fail the call. Kept in one place so
/// the tool schemas and the validation never drift.
/// </summary>
internal static class ArtifactFormat
{
    /// <summary>The canonical formats advertised in the tool schemas' enum.</summary>
    public static readonly string[] Canonical =
        ["html", "markdown", "svg", "python", "csharp", "cpp", "javascript", "rust"];

    /// <summary>Map a model-supplied format word (or alias) onto a kind, or null.</summary>
    public static ArtifactKind? ToKind(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "markdown" or "md" => ArtifactKind.Md,
            "html" or "htm" => ArtifactKind.Html,
            "svg" => ArtifactKind.Svg,
            "python" or "py" => ArtifactKind.Py,
            "csharp" or "cs" => ArtifactKind.Cs,
            "cpp" or "c++" => ArtifactKind.Cpp,
            "javascript" or "js" => ArtifactKind.Js,
            "rust" or "rs" => ArtifactKind.Rs,
            _ => null,
        };

    /// <summary>The canonical format word for a kind (stored as the language hint).</summary>
    public static string FromKind(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Md => "markdown",
        ArtifactKind.Html => "html",
        ArtifactKind.Svg => "svg",
        ArtifactKind.Py => "python",
        ArtifactKind.Cs => "csharp",
        ArtifactKind.Cpp => "cpp",
        ArtifactKind.Js => "javascript",
        ArtifactKind.Rs => "rust",
        _ => "",
    };
}
