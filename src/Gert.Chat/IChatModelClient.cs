using Gert.Model;
using Gert.Model.Chat;

namespace Gert.Chat;

/// <summary>
/// Port for the OpenAI-compatible chat-completion model (chat-and-tools.md
/// section tool loop). The real client (vLLM over <c>IHttpClientFactory</c> + Polly)
/// lives in <c>Gert.Chat</c>; tests use a fake. Streaming yields
/// content deltas and tool-call requests; the orchestrator drives the loop.
/// </summary>
public interface IChatModelClient
{
    /// <summary>Stream a completion: text deltas interleaved with tool-call requests.</summary>
    IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}
