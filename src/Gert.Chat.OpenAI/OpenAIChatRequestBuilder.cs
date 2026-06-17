using System.Globalization;
using System.Text;
using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using OpenAI.Chat;

namespace Gert.Chat.OpenAI;

/// <summary>
/// Pure, network-free mapper from the host-agnostic <see cref="ChatCompletionRequest"/>
/// port DTO to the official OpenAI SDK's <see cref="ChatMessage"/> list +
/// <see cref="ChatCompletionOptions"/> for <c>/v1/chat/completions</c>. The SDK owns the
/// wire format (so the request always matches the OpenAI spec); the two vLLM extension
/// fields Gert needs - per-message <c>reasoning_content</c> and request-level
/// <c>chat_template_kwargs</c> - ride via <c>JsonPatch</c>. Kept separate from
/// <see cref="OpenAIChatModelClient"/> so the request shaping (messages, tools, sampling
/// params) is unit-testable without an <c>HttpClient</c>.
///
/// <para>
/// The model decides tool calls: when tools are advertised we send
/// <c>tool_choice:"auto"</c> (the spec default, stated explicitly); without tools the
/// field is omitted - some servers reject <c>tool_choice</c> without <c>tools</c>.
/// </para>
/// </summary>
public static class OpenAIChatRequestBuilder
{
    /// <summary>Build the SDK message list + options for a streaming chat completion,
    /// applying the selected provider's sampling (<paramref name="parameters"/>). The
    /// model id and <c>stream</c>/<c>stream_options</c> are injected by the SDK's
    /// <c>ChatClient</c> at call time, not here.</summary>
    public static (IReadOnlyList<ChatMessage> Messages, ChatCompletionOptions Options) Build(
        ChatCompletionRequest request,
        ChatProviderParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parameters);

        var options = new ChatCompletionOptions
        {
            // OpenAI-spec sampling from the provider (null = omit -> upstream default).
            Temperature = (float?)parameters.Temperature,
            TopP = (float?)parameters.TopP,
            PresencePenalty = (float?)parameters.PresencePenalty,
            FrequencyPenalty = (float?)parameters.FrequencyPenalty,
            // max_completion_tokens is the per-round budget cap the turn runner computes
            // (TurnOptions.MaxTokensPerRound) - the one request-borne sampling field.
            MaxOutputTokenCount = request.MaxTokens,
            Seed = parameters.Seed,
            ToolChoice = request.Tools.Count > 0 ? ChatToolChoice.CreateAutoChoice() : null,
        };

        if (parameters.Stop is { Count: > 0 } stop)
        {
            foreach (var s in stop)
            {
                options.StopSequences.Add(s);
            }
        }

        foreach (var t in request.Tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                t.Name,
                t.Description,
                // ParametersSchema is a JSON-schema string; embed it verbatim.
                ParseSchema(t.ParametersSchema)));
        }

        // Everything outside the OpenAI REST spec rides JsonPatch from the provider's
        // Extra map: the vLLM sampling extensions (top_k / min_p / repetition_penalty)
        // AND the template kwargs (chat_template_kwargs.enable_thinking /
        // preserve_thinking). NOTE: template kwargs change the rendered chat template,
        // and so the vLLM prefix-cache key for the conversation - switching provider
        // mid-thread costs one full prefill. Spec-pure servers ignore unknown fields.
        ApplyExtra(options, parameters.Extra);

        return (request.Messages
            .Select(m => BuildMessage(m, parameters.PreserveThinking))
            .ToList(), options);
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

    private static ChatMessage BuildMessage(ChatModelMessage m, bool preserveThinking) => m.Role switch
    {
        "system" => new SystemChatMessage(m.Content ?? string.Empty),
        "user" => BuildUserMessage(m),
        "assistant" => BuildAssistantMessage(m, preserveThinking),
        "tool" => new ToolChatMessage(m.ToolCallId ?? string.Empty, m.Content ?? string.Empty),
        _ => throw new InvalidOperationException($"Unsupported chat role '{m.Role}'."),
    };

    private static UserChatMessage BuildUserMessage(ChatModelMessage m)
    {
        if (m.Images is not { Count: > 0 })
        {
            return new UserChatMessage(m.Content ?? string.Empty);
        }

        // Vision input: the OpenAI content-array form - the text part (when present)
        // followed by one image_url part per image, each a base64 data URL (vLLM
        // fetches nothing; the bytes ride the request).
        var parts = new List<ChatMessageContentPart>();
        if (!string.IsNullOrEmpty(m.Content))
        {
            parts.Add(ChatMessageContentPart.CreateTextPart(m.Content));
        }

        foreach (var image in m.Images)
        {
            parts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(Convert.FromBase64String(image.DataBase64)),
                image.MimeType));
        }

        return new UserChatMessage(parts);
    }

    private static AssistantChatMessage BuildAssistantMessage(ChatModelMessage m, bool preserveThinking)
    {
        // A tool-call-only assistant turn omits `content` entirely (the strictest
        // reading of the OpenAI wire format) - the SDK drops the empty collection.
        var message = m.Content is not null || m.ToolCalls is not { Count: > 0 }
            ? new AssistantChatMessage(m.Content ?? string.Empty)
            : new AssistantChatMessage(BuildToolCalls(m.ToolCalls));

        if (m.Content is not null && m.ToolCalls is { Count: > 0 })
        {
            // A turn can carry both text and tool calls (the model "thought out loud").
            foreach (var call in BuildToolCalls(m.ToolCalls))
            {
                message.ToolCalls.Add(call);
            }
        }

        // Prior-turn thinking sent back for interleaved reasoning (vLLM extension) -
        // only when the selected provider has preserve_thinking on. The Qwen3.6
        // template re-wraps it as a <think> block; sending it to an instruct provider
        // would inject phantom reasoning. Never emit empty strings - empty <think>
        // blocks drift the rendered prompt and invalidate the vLLM prefix cache
        // (QwenLM/Qwen3.6#131).
        if (preserveThinking && !string.IsNullOrEmpty(m.ReasoningContent))
        {
            message.Patch.Set("$.reasoning_content"u8, m.ReasoningContent);
        }

        return message;
    }

    private static List<ChatToolCall> BuildToolCalls(IReadOnlyList<ChatModelToolCall> calls) =>
        calls.Select(c => ChatToolCall.CreateFunctionToolCall(
            c.Id,
            c.Name,
            // OpenAI wire format: arguments is the raw JSON *string*.
            BinaryData.FromString(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson)))
        .ToList();

    /// <summary>
    /// Validate a tool's parameter-schema string. A malformed/empty schema degrades to
    /// an empty object schema rather than throwing - a bad tool spec must not crash the
    /// whole turn (and the SDK would otherwise emit the invalid text verbatim).
    /// </summary>
    private static BinaryData ParseSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return EmptyObjectSchema();
        }

        try
        {
            using var _ = JsonDocument.Parse(schema);
            return BinaryData.FromString(schema);
        }
        catch (JsonException)
        {
            return EmptyObjectSchema();
        }
    }

    private static BinaryData EmptyObjectSchema() => BinaryData.FromString("""{"type":"object"}""");
}
