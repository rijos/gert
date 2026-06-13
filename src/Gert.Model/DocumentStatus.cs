namespace Gert.Model;

/// <summary>
/// Ingestion status of a RAG <see cref="Document"/> - mirrors <c>rag.db</c>
/// <c>documents.status</c> and drives the knowledge-panel pills
/// (chat-and-tools.md section ingestion).
/// </summary>
public enum DocumentStatus
{
    Processing,
    Ready,
    Failed,
}
