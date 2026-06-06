using System.Text.Json;
using Gert.Service.External;

namespace Gert.External.Vllm;

/// <summary>
/// Pure, network-free parser for the OpenAI-compatible streaming SSE protocol on
/// <c>/v1/chat/completions</c>. Feed it the JSON payload of each <c>data:</c> line
/// (the caller strips the <c>data: </c> prefix and skips <c>[DONE]</c>); it returns
/// zero or more <see cref="ChatModelChunk"/>s for that line and accumulates partial
/// tool-call fragments across lines.
///
/// <para>
/// <b>Tool calls</b> arrive incrementally: the first delta for a tool call carries
/// <c>id</c> + <c>function.name</c>, later deltas append <c>function.arguments</c>
/// fragments, all keyed by <c>index</c>. We buffer per-index and only emit a
/// <see cref="ChatModelToolCall"/> when the stream finishes that call — at the
/// terminal chunk (a non-null <c>finish_reason</c>), the buffered calls are flushed
/// before the finish chunk. <b>finish_reason</b> and <b>usage</b> come on the final
/// chunk(s): <c>finish_reason</c> on the last choice, <c>usage</c> on the trailing
/// usage-only chunk (vLLM sends it last when <c>stream_options.include_usage</c> is
/// set). The terminal <see cref="ChatModelChunk"/> carries both.
/// </para>
///
/// <para>Stateful — one instance per stream; not thread-safe.</para>
/// </summary>
public sealed class VllmStreamParser
{
    // Buffered tool calls keyed by their streamed index, in arrival order.
    private readonly SortedDictionary<int, ToolCallBuffer> _toolCalls = new();
    private string? _finishReason;
    private int? _completionTokens;
    private int? _promptTokens;
    private bool _finished;

    /// <summary>
    /// Parse one SSE data payload. Returns the chunks to surface for this line. The
    /// terminal finish chunk is only returned once <see cref="Finish"/> sees a
    /// <c>finish_reason</c>; usage may follow on a later line and is folded in.
    /// </summary>
    public IReadOnlyList<ChatModelChunk> Parse(string dataJson)
    {
        ArgumentNullException.ThrowIfNull(dataJson);

        var chunks = new List<ChatModelChunk>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataJson);
        }
        catch (JsonException)
        {
            // A malformed line is ignored rather than aborting the whole stream.
            return chunks;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Usage may arrive on a trailing usage-only chunk (empty choices).
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("completion_tokens", out var ct) &&
                    ct.ValueKind == JsonValueKind.Number)
                {
                    _completionTokens = ct.GetInt32();
                }

                if (usage.TryGetProperty("prompt_tokens", out var pt) &&
                    pt.ValueKind == JsonValueKind.Number)
                {
                    _promptTokens = pt.GetInt32();
                }

                // vLLM sends the usage tail AFTER the finish_reason chunk, so the
                // finish chunk has already gone out without it — surface a trailing
                // usage chunk so the runner still observes the counts.
                if (_finished && (_completionTokens is not null || _promptTokens is not null))
                {
                    chunks.Add(new ChatModelChunk
                    {
                        TokenCount = _completionTokens,
                        PromptTokenCount = _promptTokens,
                    });
                }
            }

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    ParseChoice(choice, chunks);
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Flush any buffered tool calls + the terminal finish chunk. Call once after the
    /// stream ends (e.g. on <c>[DONE]</c> or stream close) to surface a finish chunk
    /// even if the server omitted a usage tail. Idempotent: returns nothing if the
    /// terminal chunk was already emitted inline.
    /// </summary>
    public IReadOnlyList<ChatModelChunk> Flush()
    {
        if (_finished)
        {
            return [];
        }

        var chunks = new List<ChatModelChunk>();
        FlushToolCalls(chunks);
        EmitFinish(chunks);
        return chunks;
    }

    private void ParseChoice(JsonElement choice, List<ChatModelChunk> chunks)
    {
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object)
        {
            // Reasoning ("thinking") delta — emitted by --reasoning-parser
            // before the answer's content deltas.
            if (delta.TryGetProperty("reasoning_content", out var reasoningContent) &&
                reasoningContent.ValueKind == JsonValueKind.String)
            {
                var thought = reasoningContent.GetString();
                if (!string.IsNullOrEmpty(thought))
                {
                    chunks.Add(new ChatModelChunk { ReasoningDelta = thought });
                }
            }

            // Content delta.
            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    chunks.Add(new ChatModelChunk { TextDelta = text });
                }
            }

            // Tool-call fragments — buffer by index.
            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    BufferToolCallFragment(tc);
                }
            }
        }

        // finish_reason on the last choice ends the turn.
        if (choice.TryGetProperty("finish_reason", out var fr) &&
            fr.ValueKind == JsonValueKind.String)
        {
            _finishReason = fr.GetString();

            // Flush buffered tool calls, then the finish chunk. Usage may still arrive
            // on a later usage-only chunk; vLLM sends the usage tail AFTER the
            // finish_reason chunk, so we cannot wait for it here. We emit finish now
            // and accept that token count is best-effort (folded if it was already seen).
            FlushToolCalls(chunks);
            EmitFinish(chunks);
        }
    }

    private void BufferToolCallFragment(JsonElement tc)
    {
        var index = tc.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
            ? idx.GetInt32()
            : 0;

        if (!_toolCalls.TryGetValue(index, out var buffer))
        {
            buffer = new ToolCallBuffer();
            _toolCalls[index] = buffer;
        }

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            buffer.Id = id.GetString();
        }

        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                buffer.Name = name.GetString();
            }

            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
            {
                buffer.Arguments.Append(args.GetString());
            }
        }
    }

    private void FlushToolCalls(List<ChatModelChunk> chunks)
    {
        foreach (var buffer in _toolCalls.Values)
        {
            if (string.IsNullOrEmpty(buffer.Name))
            {
                continue;
            }

            chunks.Add(new ChatModelChunk
            {
                ToolCall = new ChatModelToolCall
                {
                    Id = buffer.Id ?? $"call_{buffer.Name}",
                    Name = buffer.Name,
                    ArgumentsJson = buffer.Arguments.Length == 0 ? "{}" : buffer.Arguments.ToString(),
                },
            });
        }

        _toolCalls.Clear();
    }

    private void EmitFinish(List<ChatModelChunk> chunks)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        chunks.Add(new ChatModelChunk
        {
            FinishReason = _finishReason ?? "stop",
            TokenCount = _completionTokens,
            PromptTokenCount = _promptTokens,
        });
    }

    /// <summary>Per-index accumulator for a streamed tool call.</summary>
    private sealed class ToolCallBuffer
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public System.Text.StringBuilder Arguments { get; } = new();
    }
}
