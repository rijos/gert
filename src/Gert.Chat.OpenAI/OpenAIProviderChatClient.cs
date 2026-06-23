using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Gert.Chat.OpenAI;

/// <summary>
/// The Gert-specific chat behaviour the stock Microsoft.Extensions.AI OpenAI adapter has no
/// equivalent for, layered as a <see cref="DelegatingChatClient"/> over
/// <c>chatClient.AsIChatClient()</c> (decisions #13). It owns three things the adapter cannot:
/// <list type="bullet">
/// <item><b>The provider's sampling + vendor extensions</b>: the selected provider's typed sampling
/// (temperature/top_p/penalties/seed/stop) plus the off-spec <c>Extra</c> map (vLLM
/// <c>top_k</c>/<c>min_p</c>/<c>repetition_penalty</c> and the <c>chat_template_kwargs</c>) ride a
/// <see cref="ChatOptions.RawRepresentationFactory"/> seeding an OpenAI SDK
/// <see cref="ChatCompletionOptions"/> with the same JsonPatch the old request builder used. Sampling
/// rides the provider, not the request (chat-and-tools.md section mode-correct sampling).</item>
/// <item><b>Interleaved-thinking replay</b>: when the provider has <c>preserve_thinking</c> on, a
/// prior assistant turn's reasoning is sent back as the wire <c>reasoning_content</c> via a native
/// <see cref="AssistantChatMessage"/> on the message's <c>RawRepresentation</c> (the adapter drops a
/// plain <see cref="TextReasoningContent"/> on input); an instruct provider sends nothing.</item>
/// <item><b>Stream re-mapping</b> (<see cref="OpenAIStreamParser"/>): each streamed update is
/// re-parsed from its raw <see cref="StreamingChatCompletionUpdate"/> to surface vLLM's
/// <c>delta.reasoning</c> (the adapter surfaces only <c>reasoning_content</c>) and the name-first
/// live-intent signal (the adapter emits only the completed call).</item>
/// </list>
/// The transport, SSE framing, and message/tool/sampling mapping stay the adapter's job; this client
/// is pure post-/pre-processing. One instance per provider slug (carries that provider's parameters).
/// </summary>
internal sealed class OpenAIProviderChatClient : DelegatingChatClient
{
    private readonly ChatProviderParameters _parameters;
    private readonly ILogger<OpenAIProviderChatClient> _logger;

    public OpenAIProviderChatClient(
        IChatClient inner,
        ChatProviderParameters parameters,
        ILogger<OpenAIProviderChatClient> logger)
        : base(inner)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var parser = new OpenAIStreamParser();
        var effectiveMessages = ApplyThinkingReplay(messages);
        var effectiveOptions = WithProviderSampling(options);

        await using var enumerator = InnerClient
            .GetStreamingResponseAsync(effectiveMessages, effectiveOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ClientResultException ex)
            {
                // The error body carries the actual diagnostic (template/validation message) - a bare
                // status code is undebuggable from the event log. Re-thrown as HttpRequestException to
                // keep the port's error contract (TurnRunner finalises the row error + logs detail).
                var detail = ErrorDetail(ex);
                _logger.LogWarning(
                    "Chat completion failed with status {Status}: {Detail}.", ex.Status, detail);
                throw new HttpRequestException(
                    $"Chat completion failed with status {ex.Status}: {detail}",
                    ex,
                    ex.Status > 0 ? (HttpStatusCode)ex.Status : null);
            }

            if (!moved)
            {
                break;
            }

            // The adapter yields one update per SSE chunk carrying the raw SDK update, plus a
            // synthetic trailing update for its own coalesced tool call (RawRepresentation null) -
            // we re-parse the raw chunks ourselves and skip the synthetic one.
            if (enumerator.Current.RawRepresentation is not StreamingChatCompletionUpdate raw)
            {
                continue;
            }

            foreach (var u in parser.Parse(raw))
            {
                yield return u;
            }
        }

        // Surface a terminal finish update if the server closed without one inline.
        foreach (var u in parser.Flush())
        {
            yield return u;
        }
    }

    /// <summary>
    /// Build the effective <see cref="ChatOptions"/>: keep the caller's request fields (the
    /// advertised tools + the per-round <c>max_completion_tokens</c>) and overlay the selected
    /// provider's sampling. The off-spec <c>Extra</c> map rides a
    /// <see cref="ChatOptions.RawRepresentationFactory"/> seeding an OpenAI SDK
    /// <see cref="ChatCompletionOptions"/> with the same JsonPatch the old request builder applied;
    /// the adapter merges the M.E.AI-mapped fields on top of it, so both reach the wire. NOTE:
    /// template kwargs change the rendered chat template - and so the vLLM prefix-cache key for the
    /// conversation - switching provider mid-thread costs one full prefill.
    /// </summary>
    private ChatOptions WithProviderSampling(ChatOptions? options)
    {
        var effective = options is null ? new ChatOptions() : options.Clone();

        // OpenAI-spec sampling from the provider (null = omit -> upstream default).
        effective.Temperature = (float?)_parameters.Temperature;
        effective.TopP = (float?)_parameters.TopP;
        effective.PresencePenalty = (float?)_parameters.PresencePenalty;
        effective.FrequencyPenalty = (float?)_parameters.FrequencyPenalty;
        effective.Seed = _parameters.Seed;
        if (_parameters.Stop is { Count: > 0 } stop)
        {
            effective.StopSequences = [.. stop];
        }

        var extra = _parameters.Extra;
        effective.RawRepresentationFactory = _ =>
        {
            var sdk = new ChatCompletionOptions();
            ApplyExtra(sdk, extra);
            return sdk;
        };

        return effective;
    }

    /// <summary>
    /// Apply the provider's off-spec <c>Extra</c> map as root-level JsonPatch writes
    /// (<c>$.{key}</c>; dotted keys nest, e.g. <c>chat_template_kwargs.enable_thinking</c>).
    /// Each string value is parsed to its JSON type so <c>top_k</c> stays an integer and
    /// <c>enable_thinking</c> a bool; an unparseable value rides as a JSON string.
    /// </summary>
    private static void ApplyExtra(ChatCompletionOptions options, IReadOnlyDictionary<string, string> extra)
    {
        foreach (var (key, raw) in extra)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var path = Encoding.UTF8.GetBytes("$." + key);
            if (bool.TryParse(raw, out var b))
            {
                options.Patch.Set(path, b);
            }
            else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                     && l is >= int.MinValue and <= int.MaxValue)
            {
                options.Patch.Set(path, (int)l);
            }
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                options.Patch.Set(path, d);
            }
            else
            {
                options.Patch.Set(path, raw);
            }
        }
    }

    /// <summary>
    /// Interleaved-thinking replay: only when the provider has <c>preserve_thinking</c> on, rewrite
    /// each assistant message that carries reasoning into one whose <c>RawRepresentation</c> is a
    /// native <see cref="AssistantChatMessage"/> with the <c>reasoning_content</c> wire field set
    /// (the adapter honours a native RawRepresentation and drops a plain TextReasoningContent). The
    /// Qwen3.6 template re-wraps it as a <c>&lt;think&gt;</c> block; an instruct provider gets the
    /// message untouched, so the adapter simply omits the reasoning. Never emit an empty
    /// <c>reasoning_content</c> - an empty <c>&lt;think&gt;</c> block drifts the rendered prompt and
    /// invalidates the vLLM prefix cache.
    /// </summary>
    private IEnumerable<ChatMessage> ApplyThinkingReplay(IEnumerable<ChatMessage> messages)
    {
        if (!_parameters.PreserveThinking)
        {
            return messages;
        }

        return messages.Select(ToReplayMessage);
    }

    private static ChatMessage ToReplayMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant)
        {
            return message;
        }

        var reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(r => r.Text));

        // Nothing to replay, or a tool-call turn (history is role+content only, so this is defensive):
        // leave it for the adapter rather than risk dropping tool calls in the native rebuild.
        if (string.IsNullOrEmpty(reasoning) || message.Contents.OfType<FunctionCallContent>().Any())
        {
            return message;
        }

        var content = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        var native = new AssistantChatMessage(content);
        native.Patch.Set("$.reasoning_content"u8, reasoning);
        return new ChatMessage(ChatRole.Assistant, content) { RawRepresentation = native };
    }

    /// <summary>
    /// Best-effort extraction of the error response body, bounded so a misbehaving
    /// server can't balloon the exception message.
    /// </summary>
    private static string ErrorDetail(ClientResultException ex)
    {
        string detail;
        try
        {
            detail = ex.GetRawResponse()?.Content.ToString()?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            detail = string.Empty;
        }

        if (detail.Length == 0)
        {
            detail = ex.Message;
        }

        return detail.Length > 512 ? detail[..512] + "..." : detail;
    }
}
