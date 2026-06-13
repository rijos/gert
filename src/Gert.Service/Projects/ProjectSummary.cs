using Gert.Model.Projects;

namespace Gert.Service.Projects;

/// <summary>
/// A project's config plus its rollup counts - the <c>GET /api/projects</c> /
/// <c>GET /api/projects/{pid}</c> shape (rest-api.md section projects): a FLAT
/// <c>{ id, name, ..., counts, updated_at }</c> record, not a nested meta.
/// </summary>
public sealed record ProjectSummary
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

    public int ConversationCount { get; init; }

    public int DocumentCount { get; init; }

    public int MemoryCount { get; init; }

    /// <summary>Build the wire summary from on-disk config + rollup counts.</summary>
    public static ProjectSummary From(
        ProjectMeta meta,
        int conversationCount,
        int documentCount,
        int memoryCount) => new()
    {
        Id = meta.Id,
        Name = meta.Name,
        Description = meta.Description,
        Instructions = meta.Instructions,
        Defaults = meta.Defaults,
        CreatedAt = meta.CreatedAt,
        UpdatedAt = meta.UpdatedAt,
        ConversationCount = conversationCount,
        DocumentCount = documentCount,
        MemoryCount = memoryCount,
    };
}
