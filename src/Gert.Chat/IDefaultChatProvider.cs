using Gert.Model.Chat;

namespace Gert.Chat;

/// <summary>
/// Supplies the single synthesized provider used when <c>Gert:Chat:Providers</c> is empty -
/// the zero-config boot (configuration.md section providers). The generic
/// <see cref="ConfigChatProviderCatalog"/> stays implementation-agnostic: it does not know
/// where a default's connection comes from, so an implementation plugin contributes one (the
/// OpenAI plugin points it at <c>Gert:Embeddings:Parameters:BaseUrl</c>, so a single-vLLM
/// deployment serves chat and embeddings from one base URL). When no plugin registers a
/// default, an empty <c>Gert:Chat:Providers</c> yields an empty catalog.
/// </summary>
public interface IDefaultChatProvider
{
    /// <summary>
    /// The synthesized default provider's secret-free catalog entry (its <c>Default</c> flag set),
    /// or <c>null</c> for none. The matching connection + sampling are wired by the same plugin,
    /// keyed by <see cref="ChatProviderInfo.DefaultId"/>, so the catalog and the factory agree.
    /// </summary>
    ChatProviderInfo? Synthesize();
}
