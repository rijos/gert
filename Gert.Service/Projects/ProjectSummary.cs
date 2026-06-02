using Gert.Model.Projects;

namespace Gert.Service.Projects;

/// <summary>
/// A project's config plus its rollup counts — the <c>GET /api/projects</c> /
/// <c>GET /api/projects/{pid}</c> shape (rest-api.md § projects).
/// </summary>
public sealed record ProjectSummary
{
    public required ProjectMeta Meta { get; init; }

    public int ConversationCount { get; init; }

    public int DocumentCount { get; init; }

    public int MemoryCount { get; init; }
}
