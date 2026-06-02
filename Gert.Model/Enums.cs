namespace Gert.Model;

/// <summary>
/// Role of a chat message — mirrors <c>chat.db</c> <c>messages.role</c>
/// (storage-and-data.md § chat.db).
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    System,
    Tool,
}

/// <summary>
/// Lifecycle status of a <see cref="ToolCall"/> — mirrors <c>chat.db</c>
/// <c>tool_calls.status</c>.
/// </summary>
public enum ToolCallStatus
{
    Running,
    Done,
    Error,
}

/// <summary>
/// Source of a <see cref="Citation"/> — mirrors <c>chat.db</c>
/// <c>citations.source_type</c>.
/// </summary>
public enum CitationSourceType
{
    Document,
    Web,
}

/// <summary>
/// Canvas-tab artifact kind — mirrors <c>chat.db</c> <c>artifacts.kind</c>
/// (storage-and-data.md § chat.db).
/// </summary>
public enum ArtifactKind
{
    Md,
    Html,
    Svg,
    Py,
}

/// <summary>
/// Ingestion status of a RAG <see cref="Document"/> — mirrors <c>rag.db</c>
/// <c>documents.status</c> and drives the knowledge-panel pills
/// (chat-and-tools.md § ingestion).
/// </summary>
public enum DocumentStatus
{
    Processing,
    Ready,
    Failed,
}

/// <summary>
/// Whether a RAG row is an uploaded document or a memory entry — mirrors
/// <c>rag.db</c> <c>documents.kind</c> (configuration.md § 2.3).
/// </summary>
public enum DocumentKind
{
    Document,
    Memory,
}

/// <summary>
/// User-level memory-write mode — mirrors <c>settings.json</c> (configuration.md § 2.3):
/// whether the assistant may author memory entries itself.
/// </summary>
public enum MemoryMode
{
    Off,
    Manual,
    Auto,
}

/// <summary>
/// UI colour theme — mirrors <c>settings.json</c> <c>theme</c> (configuration.md § 3.1).
/// </summary>
public enum Theme
{
    Light,
    Dark,
    Auto,
}
