namespace Gert.Rag;

/// <summary>
/// The RAG engine selection (<c>Gert:Rag</c>): the uniform "functionality -> Type"
/// shape (configuration.md section 4; tech-stack.md section Architecture). RAG is its
/// own capability, decoupled from the SQL database engine - a vector index is not
/// naturally a SQL concern. One engine sits behind <see cref="IRagIndexProvider"/>:
/// <c>Sqlite</c> ships today (per-project <c>rag.db</c> with sqlite-vec + FTS5; the
/// default). A dedicated vector store (e.g. <c>Qdrant</c>) is a sibling
/// <c>Gert.Rag.*</c> plugin selected by the same <see cref="Type"/> token, with no
/// central <c>switch</c>. Case-insensitive; a value with no registered plugin fails
/// fast at first resolution (see <see cref="RagEngineFactory"/>).
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Gert:Rag";

    /// <summary>
    /// Which engine the RAG index resolves to: <c>Sqlite</c> (default - per-project
    /// sqlite-vec + FTS5 under the <c>Storage</c> data-root). Case-insensitive; an
    /// unknown value fails fast at first resolution with an actionable message.
    /// </summary>
    public string Type { get; set; } = RagEngineFactory.DefaultType;
}
