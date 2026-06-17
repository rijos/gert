using Gert.Model.Plugins;

namespace Gert.Rag;

/// <summary>
/// A RAG-engine plugin: builds the <see cref="IRagIndexProvider"/> for one engine
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>Sqlite</c>). Each implementation
/// assembly registers exactly one, keyed by its Type; the generic
/// <see cref="RagEngineFactory"/> resolves the builder for the configured
/// <c>Gert:Rag:Type</c> and delegates - there is no central <c>switch</c> over Type
/// (mirrors <c>Gert.Database.IDatabaseEngineBuilder</c> / <c>Gert.Chat.IChatModelClientBuilder</c>).
/// The builder owns its engine's connection mechanics (a file path for sqlite-vec, a
/// collection/endpoint for a remote vector store), so the contracts assembly stays
/// engine-agnostic.
/// </summary>
public interface IRagEngineBuilder : ICapabilityPlugin
{
    /// <summary>Build the <see cref="IRagIndexProvider"/> for this engine.</summary>
    IRagIndexProvider BuildRagIndexProvider();
}
