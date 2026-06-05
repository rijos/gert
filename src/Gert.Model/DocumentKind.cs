namespace Gert.Model;

/// <summary>
/// Whether a RAG row is an uploaded document or a memory entry — mirrors
/// <c>rag.db</c> <c>documents.kind</c> (configuration.md § 2.3).
/// </summary>
public enum DocumentKind
{
    Document,
    Memory,
}
