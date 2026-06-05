using System.Text.RegularExpressions;
using Gert.Model;

namespace Gert.Service.Chat;

/// <summary>
/// Extracts canvas artifacts from a completed assistant message (the
/// "citation/artifact extraction" of implementation-plan U7b). The model opts a
/// fenced block into the canvas by naming it in the fence info string:
///
/// <code>
/// ```html name=demo.html
/// &lt;h1&gt;hi&lt;/h1&gt;
/// ```
/// </code>
///
/// Only fences that carry a <c>name=</c> token become artifacts — an ordinary
/// code block stays inline in the bubble. The language token maps onto the
/// closed <see cref="ArtifactKind"/> set (md/html/svg/py); an unknown language
/// is left inline too, never guessed. Pure and deterministic: same content in,
/// same artifacts out — which is what lets the fixture-driven E2E assert on it.
/// </summary>
public static partial class ArtifactExtractor
{
    /// <summary>One extracted artifact, pre-persistence (no ids/timestamps yet).</summary>
    public sealed record ExtractedArtifact
    {
        public required ArtifactKind Kind { get; init; }

        public required string Name { get; init; }

        public required string Language { get; init; }

        public required string Content { get; init; }
    }

    // ```<lang> name=<file>\n<body>\n``` — opening and closing fences each on
    // their own line. Multiline anchors the fences at line starts; Singleline
    // lets the lazy body span lines.
    [GeneratedRegex(
        @"^```(?<lang>[A-Za-z0-9_+-]+)[ \t]+name=(?<name>[^\s`]+)[ \t]*\r?\n(?<body>.*?)\r?\n```[ \t]*(?:\r?\n|$)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex NamedFence();

    /// <summary>
    /// Pull every named, known-kind fenced block out of <paramref name="content"/>.
    /// Duplicate names collapse to the LAST occurrence (the model "saved over" the
    /// earlier draft), keeping first-seen order — one canvas tab per name.
    /// </summary>
    public static IReadOnlyList<ExtractedArtifact> Extract(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        // name → artifact, insertion-ordered; a re-used name overwrites in place.
        var byName = new Dictionary<string, ExtractedArtifact>(StringComparer.Ordinal);
        foreach (Match match in NamedFence().Matches(content))
        {
            var language = match.Groups["lang"].Value;
            if (ResolveKind(language) is not { } kind)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            byName[name] = new ExtractedArtifact
            {
                Kind = kind,
                Name = name,
                Language = language,
                Content = match.Groups["body"].Value,
            };
        }

        return byName.Values.ToList();
    }

    /// <summary>Map a fence language token onto the closed artifact-kind set.</summary>
    private static ArtifactKind? ResolveKind(string language) =>
        language.ToLowerInvariant() switch
        {
            "md" or "markdown" => ArtifactKind.Md,
            "html" or "htm" => ArtifactKind.Html,
            "svg" => ArtifactKind.Svg,
            "py" or "python" => ArtifactKind.Py,
            _ => null,
        };
}
