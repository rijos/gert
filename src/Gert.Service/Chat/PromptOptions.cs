namespace Gert.Service.Chat;

/// <summary>
/// Operator-configurable system-prompt fragments (bound from <c>Gert:Prompts</c>).
/// Defaults are empty here - the host's appsettings supplies the real text, and tests
/// set their own. An empty <see cref="Canvas"/> omits the nudge entirely.
/// </summary>
public sealed class PromptOptions
{
    /// <summary>
    /// The canvas/artifact convention prepended to every turn's system prompt (before
    /// project pinned instructions). Steers the model to USE the make/edit/read artifact
    /// tools instead of pasting whole files into code blocks. Empty omits it. Editing it
    /// invalidates the vLLM prefix cache for all conversations at once (it is the first
    /// thing in every rendered prompt).
    /// </summary>
    public string Canvas { get; set; } = string.Empty;
}
