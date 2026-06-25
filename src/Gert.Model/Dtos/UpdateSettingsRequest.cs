namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>PUT /api/settings</c> (rest-api.md section settings) - update any
/// subset of the user's settings (the <c>user.db</c> settings row; configuration.md section 3). All fields
/// optional; an absent field means "leave unchanged". (Theme is device-local - localStorage,
/// not a server setting - so it is not part of this request.)
/// </summary>
public sealed record UpdateSettingsRequest
{
    public string? UiLanguage { get; init; }

    public string? ReplyLanguage { get; init; }

    public string? DefaultModelId { get; init; }

    public ToolToggles? DefaultTools { get; init; }
}
