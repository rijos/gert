using Gert.Model.Projects;

namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>PATCH /api/projects/{pid}</c> (rest-api.md section projects):
/// rename / edit instructions / edit defaults. All fields optional.
/// </summary>
public sealed record UpdateProjectRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public ProjectDefaults? Defaults { get; init; }
}
