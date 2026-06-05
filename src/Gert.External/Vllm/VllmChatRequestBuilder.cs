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
            var msg = new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            };
            if (!string.IsNullOrEmpty(m.ToolCallId))
            {
                msg["tool_call_id"] = m.ToolCallId;
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
