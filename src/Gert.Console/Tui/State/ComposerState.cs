using Gert.Model.Dtos;

namespace Gert.Console.Tui.State;

/// <summary>
/// The composer's send-time options (U16) — the console analog of the SPA's
/// composer state: selected model, tool toggles, thinking flags. The TUI
/// always sends explicit toggles (request wins over conversation defaults in
/// the planner), so what the menu shows is exactly what the turn gets.
/// </summary>
public sealed class ComposerState
{
    private readonly Dictionary<string, bool> _tools = new(StringComparer.Ordinal);

    /// <summary>Selected model id; null inherits the conversation/server default.</summary>
    public string? ModelId { get; set; }

    /// <summary>Reasoning on/off (web default: on).</summary>
    public bool Thinking { get; set; } = true;

    /// <summary>Interleaved thinking — carry prior reasoning into the next turn.</summary>
    public bool PreserveThinking { get; set; }

    /// <summary>The tool id → enabled map shown in the tools menu.</summary>
    public IReadOnlyDictionary<string, bool> Tools => _tools;

    /// <summary>Seed the toggle set from the registry ids (all on by default —
    /// the console's single local user owns the machine).</summary>
    public void SeedTools(IEnumerable<string> toolIds, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        foreach (var id in toolIds)
        {
            _tools.TryAdd(id, enabled);
        }
    }

    /// <summary>Flip one tool.</summary>
    public void SetTool(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _tools[id] = enabled;
    }

    /// <summary>The explicit per-request toggles for <see cref="SendMessageRequest.Tools"/>.</summary>
    public ToolToggles ToToolToggles() => new(_tools);
}
