namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>PUT /api/settings</c> (rest-api.md § settings) — update any
/// subset of the user's <c>settings.json</c> (configuration.md § 3). All fields
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

    /// <summary>
    /// Per-model generation defaults to merge: each supplied model id REPLACES
    /// that model's entry (an empty params object clears it); absent ids stay.
    /// </summary>
    public IReadOnlyDictionary<string, GenerationParams>? ModelParams { get; init; }
}
