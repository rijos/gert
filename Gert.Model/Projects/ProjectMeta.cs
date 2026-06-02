namespace Gert.Model.Projects;

/// <summary>
/// On-disk project configuration — the <c>projects/{pid}/meta.json</c> shape
/// <c>{ id, name, description, instructions, model_id?, tools?, params?,
/// reply_language?, created_at, updated_at }</c> (configuration.md § 2.4).
/// Config lives in the filesystem, not the databases.
/// </summary>
public sealed record ProjectMeta
{
    /// <summary>Project id — a UUID, or the literal <c>default</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The always-injected, length-bounded custom system prompt (configuration.md § 2.3).</summary>
    public string? Instructions { get; init; }

    /// <summary>Defaults seeded into new conversations (the cascade level).</summary>
    public ProjectDefaults? Defaults { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
