using Gert.Model.Dtos;

namespace Gert.Model.Projects;

/// <summary>
/// Project-level defaults in the configuration cascade - the <c>defaults</c>
/// shape <c>{ model_id?, tools?, reply_language? }</c> (rest-api.md section projects;
/// configuration.md section 1). Stored as the <c>defaults_json</c> column on the project's
/// registry row in <c>user.db</c>. Unset fields inherit the user/server level. (Sampling is
/// not a cascade level - it rides the selected provider, configuration.md section providers.)
/// </summary>
public sealed record ProjectDefaults
{
    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    public string? ReplyLanguage { get; init; }
}
