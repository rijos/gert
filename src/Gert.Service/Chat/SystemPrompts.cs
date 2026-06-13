namespace Gert.Service.Chat;

/// <summary>
/// Built-in system-prompt fragments every turn carries (step 0). Project pinned
/// instructions are appended AFTER these. Keep the text STABLE - it is the
/// first thing in every rendered prompt, so any edit invalidates the vLLM
/// prefix cache for all conversations at once.
/// </summary>
public static class SystemPrompts
{
    /// <summary>
    /// The canvas/artifact convention (chat-and-tools.md section artifacts). Artifacts
    /// now reach the canvas through the make/edit/read tools, not fenced blocks -
    /// a tool argument can't be truncated by the file's own ``` fences. This nudge
    /// steers the model to USE the tools (real models sometimes answer a "make a
    /// file" request in prose otherwise); the per-tool descriptions carry the
    /// argument detail.
    /// </summary>
    public const string Canvas =
        "When you produce a complete, self-contained file (an HTML page, a script, a Markdown " +
        "document, an SVG, etc.), call the make_artifact tool with the whole file content - it " +
        "opens in the user's canvas. Do not paste a whole file into a code block. To change an " +
        "existing artifact, use edit_artifact to replace just the part that changes rather than " +
        "remaking the whole file; use read_artifact to see its current content first if needed. " +
        "Keep ordinary code blocks for short inline snippets and examples.";
}
