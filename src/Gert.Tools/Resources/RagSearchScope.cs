namespace Gert.Tools;

/// <summary>
/// What a RAG search ranges over. A record (not a bare enum) so a Chat case carrying a
/// conversation handle is additive later without breaking the <see cref="IRagResource"/> contract.
/// </summary>
public sealed record RagSearchScope(RagSearchScopeKind Kind)
{
    /// <summary>The project's whole RAG corpus.</summary>
    public static RagSearchScope Project { get; } = new(RagSearchScopeKind.Project);
}
