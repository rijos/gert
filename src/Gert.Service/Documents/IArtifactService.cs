using Gert.Model.Chat;

namespace Gert.Service.Documents;

/// <summary>
/// Reads chat artifacts (the canvas tabs), produced during chat and stored in
/// the project's <c>chat.db</c> (rest-api.md section artifacts).
/// </summary>
public interface IArtifactService
{
    /// <summary>List a conversation's artifacts for the canvas tab strip.</summary>
    Task<IReadOnlyList<Artifact>> ListAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Get one artifact's raw content (download / "Source" view).</summary>
    Task<Artifact?> GetAsync(
        string pid,
        string artifactId,
        CancellationToken cancellationToken = default);
}
