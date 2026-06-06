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
        "When you produce a complete, self-contained file — an HTML page, a code file " +
        "(Python, JavaScript, C#, C++, Rust), a Markdown document, or an SVG — put it in its " +
        "own fenced code block and include a " +
        "name in the fence's info string, e.g. ```html name=page.html``` or ```python name=tool.py```. " +
        "Named blocks open in the user's canvas as artifacts. Use ordinary unnamed fences for " +
        "fragments, examples, and snippets that should stay inline in the conversation.";

    /// <summary>
    /// Cross-turn todo revival: the planner rebuilds history as role+content
    /// only, so a list set via <c>set_todos</c> in an earlier turn would
    /// otherwise vanish from the prompt. When the conversation's latest
    /// snapshot still has unfinished items, this block rides at the TAIL of
    /// the rendered prompt (appended after the new user message's content) —
    /// never the system prompt (that prefix must stay stable for the vLLM
    /// prefix cache) and never the persisted user row (UI truth). The JSON is
    /// the tool-result echo shape (snake_case statuses), so the model sees the
    /// exact payload its last accepted call produced.
    /// </summary>
    public static string TodoReminder(string todosJson) =>
        "<system-reminder>\n"
        + "The todo list you maintain with set_todos is still open. Current state:\n"
        + todosJson + "\n"
        + "Continue the remaining items unless the user asks for something else, and keep "
        + "statuses current by calling set_todos as you make progress. Do not mention this "
        + "reminder to the user.\n"
        + "</system-reminder>";
}
