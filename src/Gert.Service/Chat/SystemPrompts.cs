namespace Gert.Service.Chat;

/// <summary>
/// Built-in system-prompt fragments every turn carries (step 0). Project pinned
/// instructions are appended AFTER these. Keep the text STABLE — it is the
/// first thing in every rendered prompt, so any edit invalidates the vLLM
/// prefix cache for all conversations at once.
/// </summary>
public static class SystemPrompts
{
    /// <summary>
    /// The canvas/artifact convention (chat-and-tools.md § artifacts): the
    /// extractor only lifts fences whose info string carries <c>name=</c>, so
    /// the model must be told the opt-in syntax — without this, real models
    /// emit plain fences and complete files never reach the canvas.
    /// </summary>
    public const string Canvas =
        "When you produce a complete, self-contained file — an HTML page, a Python script, " +
        "a Markdown document, or an SVG — put it in its own fenced code block and include a " +
        "name in the fence's info string, e.g. ```html name=page.html``` or ```python name=tool.py```. " +
        "Named blocks open in the user's canvas as artifacts. Use ordinary unnamed fences for " +
        "fragments, examples, and snippets that should stay inline in the conversation.";
}
