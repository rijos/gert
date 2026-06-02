using Gert.Model.Dtos;

namespace Gert.Model.Projects;

/// <summary>
/// Project-level defaults in the configuration cascade — the <c>defaults</c>
/// shape <c>{ model_id?, tools?, params?, reply_language? }</c> (rest-api.md
/// § projects; configuration.md § 1). Stored inside
/// <c>projects/{pid}/meta.json</c>. Unset fields inherit the user/server level.
/// </summary>
public sealed record ProjectDefaults
{
    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    public GenerationParams? Params { get; init; }

    public string? ReplyLanguage { get; init; }
}
