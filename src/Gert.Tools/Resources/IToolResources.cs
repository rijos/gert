namespace Gert.Tools.Resources;

/// <summary>
/// The resources a tool sees (chat-and-tools.md section tool host): metadata-aware stored
/// <see cref="Objects"/> (canvas artifacts at chat scope, files at project scope), the
/// project's read-only <see cref="Rag"/> index (passage search), and the project's read-only
/// <see cref="Documents"/> (list + full-text read). The host pre-scopes all - a tool names a
/// <see cref="ResourceScope"/>, never an identity.
/// </summary>
public interface IToolResources
{
    /// <summary>Metadata-aware named objects (create/read/list/delete, versioned).</summary>
    IObjectResource Objects { get; }

    /// <summary>The project's hybrid RAG index (read-only search).</summary>
    IRagResource Rag { get; }

    /// <summary>The project's documents (read-only list + full-text read).</summary>
    IDocumentResource Documents { get; }
}
