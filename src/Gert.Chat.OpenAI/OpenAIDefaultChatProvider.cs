using Gert.Chat;
using Gert.Model.Chat;
using Microsoft.Extensions.Options;

namespace Gert.Chat.OpenAI;

/// <summary>
/// The OpenAI plugin's contribution of the zero-config default provider
/// (<see cref="IDefaultChatProvider"/>): when <c>Gert:Chat:Providers</c> is empty, synthesize a
/// single permissive <c>OpenAI</c> provider pointed at the embeddings base URL
/// (<c>Gert:Embeddings:Parameters:BaseUrl</c>). The single-vLLM deployment this exists for
/// serves chat and embeddings from one base URL, so a bare boot still lists one real model. An
/// operator who configures <c>Gert:Chat:Providers</c> takes over completely, this entry included
/// (the catalog only consults the default when the map is empty).
/// </summary>
public sealed class OpenAIDefaultChatProvider : IDefaultChatProvider
{
    private readonly IOptions<EmbeddingsOptions> _embeddings;

    public OpenAIDefaultChatProvider(IOptions<EmbeddingsOptions> embeddings) =>
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));

    /// <inheritdoc />
    public ChatProviderInfo? Synthesize() => new()
    {
        Id = ChatProviderInfo.DefaultId,
        Name = "Default",
        Type = "openai",
        Default = true,
        Capabilities = [ChatProviderInfo.ToolsCapability, ChatProviderInfo.VisionCapability],
        Endpoint = _embeddings.Value.Parameters.BaseUrl,
    };
}
