namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/move</c>
/// (rest-api.md section conversations): relocate the conversation to another of the
/// caller's projects. Same-user only by construction - both ends resolve under
/// the token-derived folder (principles.md #3).
/// </summary>
public sealed record MoveConversationRequest
{
    /// <summary>The destination project id (a UUID or the literal <c>default</c>).</summary>
    public required string TargetPid { get; init; }
}
