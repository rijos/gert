using Gert.Model.Rag;
using Gert.Model.UI;

namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>PUT /api/settings</c> (rest-api.md section settings) - update any
/// subset of the user's settings (the <c>user.db</c> settings row; configuration.md section 3). All fields
/// optional; nullable enums distinguish "leave unchanged" from a set value.
/// </summary>
public sealed record UpdateSettingsRequest
{
    public Theme? Theme { get; init; }

    public string? UiLanguage { get; init; }

    public string? ReplyLanguage { get; init; }

    public string? DefaultModelId { get; init; }

    public ToolToggles? DefaultTools { get; init; }

    public MemoryMode? MemoryMode { get; init; }
}
