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
/// code block stays inline in the bubble, NEVER guessed (a complete-document
/// heuristic was tried and deliberately removed; see chat-and-tools.md
/// § artifacts). The model learns the opt-in from <see cref="SystemPrompts.Canvas"/>,
/// which Qwen3.6 follows reliably (measured 5/5 with thinking on, default
/// sampling) — if artifacts stop appearing, verify the system prompt actually
/// reaches the upstream request before suspecting this extractor. The language
/// token maps onto the closed <see cref="ArtifactKind"/> set (md/html/svg/py/cs/cpp/js/rs);
/// an unknown language is left inline too. Pure and deterministic: same content
/// in, same artifacts out — which is what lets the fixture-driven E2E assert on it.
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
            "cs" or "csharp" => ArtifactKind.Cs,
            "cpp" or "c++" or "cc" or "cxx" => ArtifactKind.Cpp,
            "js" or "javascript" => ArtifactKind.Js,
            "rs" or "rust" => ArtifactKind.Rs,
            _ => null,
        };
}
