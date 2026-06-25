using Gert.Model.Dtos;

namespace Gert.Model.Projects;

/// <summary>
/// User preferences, persisted as the <c>settings_json</c> blob in <c>user.db</c>'s
/// single settings row: UI language, default reply language, default model,
/// default tools (configuration.md section 3; rest-api.md section settings). Theme is a
/// device-local preference (localStorage only), so it is deliberately NOT a field here.
/// </summary>
public sealed record UserSettings
{
    /// <summary>UI language (BCP-47 tag), e.g. "en".</summary>
    public string? UiLanguage { get; init; }

    /// <summary>Default reply language for the assistant.</summary>
    public string? ReplyLanguage { get; init; }

    /// <summary>Default model id for new conversations.</summary>
    public string? DefaultModelId { get; init; }

    /// <summary>Default tool toggles for new conversations.</summary>
    public ToolToggles? DefaultTools { get; init; }
}
