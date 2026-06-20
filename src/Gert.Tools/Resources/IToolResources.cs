namespace Gert.Tools;

/// <summary>
/// The two resources a tool sees (chat-and-tools.md section tool host): metadata-aware stored
/// <see cref="Objects"/> (canvas artifacts at chat scope, memory/files at project scope) and the
/// project's read-only <see cref="Rag"/> index. The host pre-scopes both - a tool names a
/// <see cref="ResourceScope"/>, never an identity.
/// </summary>
public interface IToolResources
{
    /// <summary>Metadata-aware named objects (create/read/list/delete, versioned).</summary>
    IObjectResource Objects { get; }

    /// <summary>The project's hybrid RAG index (read-only search).</summary>
    IRagResource Rag { get; }
}
