using Gert.Model.Dtos;

namespace Gert.Model.Projects;

/// <summary>
/// On-disk user preferences — the <c>settings.json</c> shape at the user root:
/// theme, UI language, default reply language, default model, default tools,
/// memory mode (configuration.md § 3; rest-api.md § settings).
/// </summary>
public sealed record UserSettings
{
    public Theme Theme { get; init; } = Theme.Auto;

    /// <summary>UI language (BCP-47 tag), e.g. "en".</summary>
    public string? UiLanguage { get; init; }

    /// <summary>Default reply language for the assistant.</summary>
    public string? ReplyLanguage { get; init; }

    /// <summary>Default model id for new conversations.</summary>
    public string? DefaultModelId { get; init; }

    /// <summary>Default tool toggles for new conversations.</summary>
    public ToolToggles? DefaultTools { get; init; }

    public MemoryMode MemoryMode { get; init; } = MemoryMode.Manual;

    /// <summary>
    /// Per-model generation defaults, keyed by model id (the picker's cogwheel).
    /// Conversation-level params override these field-by-field; unset fields
    /// inherit the server defaults. Wire: <c>model_params</c>.
    /// </summary>
    public IReadOnlyDictionary<string, GenerationParams>? ModelParams { get; init; }
}
