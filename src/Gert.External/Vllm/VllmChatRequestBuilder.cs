using System.Text.Json;
using System.Text.Json.Nodes;
using Gert.Service.External;

namespace Gert.External.Vllm;

/// <summary>
/// Pure, network-free builder for the OpenAI-compatible <c>/v1/chat/completions</c>
/// request body. Kept separate from <see cref="VllmChatModelClient"/> so the request
/// shaping (model, messages, tools, <c>stream:true</c>, sampling params) is unit-testable
/// without an <c>HttpClient</c> (see U10 tests). The model decides tool calls, so we
/// always advertise tools when present and leave <c>tool_choice</c> at the server default.
/// </summary>
public static class VllmChatRequestBuilder
{
    /// <summary>Build the JSON request node for a streaming chat completion.</summary>
    public static JsonObject Build(ChatCompletionRequest request, string modelId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(modelId);

        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var msg = new JsonObject { ["role"] = m.Role };

            // An assistant turn that only carries tool calls omits `content`
            // entirely (the strictest reading of the OpenAI wire format).
            if (m.Content is not null || m.ToolCalls is not { Count: > 0 })
            {
                msg["content"] = m.Content;
            }

            // Prior-turn thinking sent back for interleaved reasoning: the
            // Qwen3.6 template re-wraps it as a <think> block when
            // preserve_thinking is on. Never emit empty strings — empty
            // <think> blocks drift the rendered prompt and invalidate the
            // vLLM prefix cache (QwenLM/Qwen3.6#131).
            if (!string.IsNullOrEmpty(m.ReasoningContent))
            {
                msg["reasoning_content"] = m.ReasoningContent;
            }

            if (!string.IsNullOrEmpty(m.ToolCallId))
            {
                msg["tool_call_id"] = m.ToolCallId;
            }

            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var c in m.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = c.Name,
                            // OpenAI wire format: arguments is the raw JSON *string*.
                            ["arguments"] = c.ArgumentsJson,
                        },
                    });
                }

                msg["tool_calls"] = calls;
            }

            messages.Add(msg);
        }

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = messages,
            ["stream"] = true,
            // Ask the server to include usage in the final SSE chunk.
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        // ParametersSchema is a JSON-schema string; embed it as a node.
                        ["parameters"] = ParseSchema(t.ParametersSchema),
                    },
                });
            }

            body["tools"] = tools;
        }

        if (request.Temperature is { } temp)
        {
            body["temperature"] = temp;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        if (request.Stop is { Count: > 0 } stop)
        {
            var stopArray = new JsonArray();
            foreach (var s in stop)
            {
                stopArray.Add(s);
            }

            body["stop"] = stopArray;
        }

        // Template kwargs — only when explicitly set (omit = server default).
        // NOTE: these change the rendered chat template, and so the vLLM
        // prefix-cache key for the whole conversation; a mid-conversation
        // toggle costs one full prefill.
        if (request.EnableThinking is not null || request.PreserveThinking is not null)
        {
            var kwargs = new JsonObject();
            if (request.EnableThinking is { } think)
            {
                kwargs["enable_thinking"] = think;
            }

            if (request.PreserveThinking is { } preserve)
            {
                kwargs["preserve_thinking"] = preserve;
            }

            body["chat_template_kwargs"] = kwargs;
        }

        return body;
    }

    /// <summary>
    /// Parse a tool's parameter-schema string into a JSON node. A malformed/empty
    /// schema degrades to an empty object schema rather than throwing — a bad tool
    /// spec must not crash the whole turn.
    /// </summary>
    private static JsonNode ParseSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return new JsonObject { ["type"] = "object" };
        }

        try
        {
            return JsonNode.Parse(schema) ?? new JsonObject { ["type"] = "object" };
        }
        catch (JsonException)
        {
            return new JsonObject { ["type"] = "object" };
        }
    }
}
