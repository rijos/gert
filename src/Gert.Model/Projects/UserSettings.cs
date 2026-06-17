using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Model.UI;

namespace Gert.Model.Projects;

/// <summary>
/// User preferences, persisted as the <c>settings_json</c> blob in <c>user.db</c>'s
/// single settings row: theme, UI language, default reply language, default model,
/// default tools, memory mode (configuration.md section 3; rest-api.md section settings).
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
}
