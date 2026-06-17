namespace Gert.Model.Projects;

/// <summary>
/// A project's configuration: a registry row in <c>user.db</c>
/// <c>{ id, name, description, instructions, defaults (model_id?, tools?,
/// reply_language?), created_at, updated_at }</c> (configuration.md section 2.4).
/// The project's <i>data</i> is its folder; its <i>config</i> is this row.
/// </summary>
public sealed record ProjectMeta
{
    /// <summary>Project id - a UUID, or the literal <c>default</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The always-injected, length-bounded custom system prompt (configuration.md section 2.3).</summary>
    public string? Instructions { get; init; }

    /// <summary>Defaults seeded into new conversations (the cascade level).</summary>
    public ProjectDefaults? Defaults { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
