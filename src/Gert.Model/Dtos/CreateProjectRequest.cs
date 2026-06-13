using Gert.Model.Projects;

namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects</c> (rest-api.md section projects):
/// <c>{ name, description?, instructions?, defaults? }</c>.
/// </summary>
public sealed record CreateProjectRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public ProjectDefaults? Defaults { get; init; }
}
