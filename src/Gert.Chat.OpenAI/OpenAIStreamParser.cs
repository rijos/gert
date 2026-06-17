using System.Text.Json;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using OpenAI.Chat;

namespace Gert.Chat.OpenAI;

/// <summary>
/// Pure, network-free mapper from the OpenAI SDK's <see cref="StreamingChatCompletionUpdate"/>s
/// (one per SSE <c>data:</c> chunk on <c>/v1/chat/completions</c>) to
/// <see cref="ChatModelChunk"/>s. The SDK owns the SSE/JSON wire parsing; this class owns
/// everything the spec doesn't: cross-chunk accumulation and the vLLM-specific recovery
/// behaviors below.
///
/// <para>
/// <b>Tool calls</b> arrive incrementally: the first delta for a tool call carries
/// <c>id</c> + <c>function.name</c>, later deltas append <c>function.arguments</c>
/// fragments, all keyed by <c>index</c>. We buffer per-index and only emit a
/// <see cref="ChatModelToolCall"/> when the stream finishes that call - at the
/// terminal chunk (a non-null <c>finish_reason</c>), the buffered calls are flushed
/// before the finish chunk. <b>finish_reason</b> and <b>usage</b> come on the final
/// chunk(s): <c>finish_reason</c> on the last choice, <c>usage</c> on the trailing
/// usage-only chunk (vLLM sends it last when <c>stream_options.include_usage</c> is
/// set). The terminal <see cref="ChatModelChunk"/> carries both.
/// </para>
///
/// <para>
/// <b>Reasoning deltas</b> (vLLM extension): <c>--reasoning-parser</c> emits thinking as
/// <c>delta.reasoning</c> (vLLM >= 0.22) or <c>delta.reasoning_content</c> (older). Both
/// are unknown to the OpenAI spec, so they are read off the update via <c>JsonPatch</c>.
/// </para>
///
/// <para>
/// <b>Leaked tool-call markup</b> (chat-and-tools.md section tool-call robustness): when the
/// server-side tool parser fails on a near-miss call, the raw <c>&lt;tool_call&gt;</c>
/// markup leaks into <c>content</c> deltas - without intervention the user sees it
/// verbatim (or, streaming, a silently empty reply). Content is therefore routed
/// through a small hold-back state machine: text up to a potential
/// <c>&lt;tool_call&gt;</c> opener streams through (at most a 10-char tail is held
/// until disambiguated), a recognised leak segment is buffered and - at its close tag
/// or stream end - salvage-parsed into a real <see cref="ChatModelToolCall"/>; an
/// unsalvageable segment is dropped, never surfaced as text. The client logs
/// <see cref="SalvagedToolCalls"/> / <see cref="DroppedLeakChars"/> after the stream.
/// </para>
///
/// <para>Stateful - one instance per stream; not thread-safe.</para>
/// </summary>
public sealed class OpenAIStreamParser
{
    private const string LeakOpen = "<tool_call>";
    private const string LeakClose = "</tool_call>";

    /// <summary>
    /// Cap on a buffered leak segment. A segment this large is not a tool call
    /// (arguments of that size would have parsed server-side) - past it the
    /// buffer is surfaced as ordinary text rather than grow unbounded.
    /// </summary>
    private const int MaxLeakBuffer = 256 * 1024;

    // Buffered tool calls keyed by their streamed index, in arrival order.
    private readonly SortedDictionary<int, ToolCallBuffer> _toolCalls = new();

    // Hold-back state for leaked tool-call markup in content deltas.
    private readonly System.Text.StringBuilder _heldText = new();
    private bool _inLeak;
    private int _salvageCounter;

    private string? _finishReason;
    private int? _completionTokens;
    private int? _promptTokens;
    private bool _finished;

    /// <summary>Tool calls recovered from markup the server's parser missed.</summary>
    public int SalvagedToolCalls { get; private set; }

    /// <summary>Characters of unsalvageable leaked tool-call markup dropped from the text.</summary>
    public int DroppedLeakChars { get; private set; }

    /// <summary>
    /// Map one streamed update. Returns the chunks to surface for it. The terminal
    /// finish chunk is only returned once a <c>finish_reason</c> arrives; usage may
    /// follow on a later update and is folded in.
    /// </summary>
    public IReadOnlyList<ChatModelChunk> Parse(StreamingChatCompletionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var chunks = new List<ChatModelChunk>();

        // Usage may arrive on a trailing usage-only chunk (empty choices).
        if (update.Usage is { } usage)
        {
            _completionTokens = usage.OutputTokenCount;
            _promptTokens = usage.InputTokenCount;

            // vLLM sends the usage tail AFTER the finish_reason chunk, so the
            // finish chunk has already gone out without it - surface a trailing
            // usage chunk so the runner still observes the counts.
            if (_finished)
            {
                chunks.Add(new ChatModelChunk
                {
                    TokenCount = _completionTokens,
                    PromptTokenCount = _promptTokens,
                });
            }
        }

        // Reasoning ("thinking") delta - emitted by --reasoning-parser before the
        // answer's content deltas. vLLM exposes it as either `reasoning` or
        // `reasoning_content` depending on the server build; accept both. Spec-unknown
        // fields ride the Patch (SCME0001 is NoWarn'd project-wide; see the csproj note).
        if (update.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? thought)
            || update.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out thought))
        {
            if (!string.IsNullOrEmpty(thought))
            {
                chunks.Add(new ChatModelChunk { ReasoningDelta = thought });
            }
        }

        // Content deltas - routed through the leak hold-back (see class doc).
        foreach (var part in update.ContentUpdate)
        {
            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
            {
                AppendContent(part.Text, chunks);
            }
        }

        // Tool-call fragments - buffer by index.
        foreach (var fragment in update.ToolCallUpdates)
        {
            BufferToolCallFragment(fragment, chunks);
        }

        // finish_reason ends the turn.
        if (update.FinishReason is { } finishReason)
        {
            _finishReason = MapFinishReason(finishReason);

            // Settle the hold-back first: an open leak segment salvages (or drops)
            // and held normal text flushes - its chunks must precede the finish.
            DrainHeldContent(chunks);

            // Flush buffered tool calls, then the finish chunk. Usage may still arrive
            // on a later usage-only chunk; vLLM sends the usage tail AFTER the
            // finish_reason chunk, so we cannot wait for it here. We emit finish now
            // and accept that token count is best-effort (folded if it was already seen).
            FlushToolCalls(chunks);
            EmitFinish(chunks);
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
        DrainHeldContent(chunks);
        FlushToolCalls(chunks);
        EmitFinish(chunks);
        return chunks;
    }

    /// <summary>Map the SDK's finish reason back to the wire's snake_case string.</summary>
    private static string MapFinishReason(ChatFinishReason reason) => reason switch
    {
        ChatFinishReason.Stop => "stop",
        ChatFinishReason.ToolCalls => "tool_calls",
        ChatFinishReason.Length => "length",
        ChatFinishReason.ContentFilter => "content_filter",
        ChatFinishReason.FunctionCall => "function_call",
        _ => reason.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Route a content delta through the leak hold-back. In normal state, text streams
    /// through minus the longest tail that could still open a <c>&lt;tool_call&gt;</c>;
    /// once an opener completes, the segment buffers silently until its close tag (or
    /// stream end) and is salvage-parsed there.
    /// </summary>
    private void AppendContent(string text, List<ChatModelChunk> chunks)
    {
        _heldText.Append(text);

        while (true)
        {
            var held = _heldText.ToString();

            if (_inLeak)
            {
                var close = held.IndexOf(LeakClose, StringComparison.Ordinal);
                if (close < 0)
                {
                    // A segment too large to be a real call degrades to visible text -
                    // better an ugly reply than silently eating unbounded output.
                    if (_heldText.Length > MaxLeakBuffer)
                    {
                        _inLeak = false;
                        _heldText.Clear();
                        chunks.Add(new ChatModelChunk { TextDelta = LeakOpen + held });
                    }

                    return;
                }

                SalvageSegment(held[..close], chunks);
                _heldText.Clear();
                _heldText.Append(held[(close + LeakClose.Length)..]);
                _inLeak = false;
                continue;
            }

            var open = held.IndexOf(LeakOpen, StringComparison.Ordinal);
            if (open >= 0)
            {
                if (open > 0)
                {
                    chunks.Add(new ChatModelChunk { TextDelta = held[..open] });
                }

                _heldText.Clear();
                _heldText.Append(held[(open + LeakOpen.Length)..]);
                _inLeak = true;
                continue;
            }

            // Emit everything except the longest suffix that is a prefix of the
            // opener - that tail stays held until the next delta disambiguates it.
            var hold = LongestOpenerPrefixSuffix(held);
            if (held.Length > hold)
            {
                chunks.Add(new ChatModelChunk { TextDelta = held[..^hold] });
                _heldText.Clear();
                _heldText.Append(held[^hold..]);
            }

            return;
        }
    }

    /// <summary>Length of the longest suffix of <paramref name="text"/> that is a proper prefix of the opener.</summary>
    private static int LongestOpenerPrefixSuffix(string text)
    {
        var max = Math.Min(text.Length, LeakOpen.Length - 1);
        for (var len = max; len > 0; len--)
        {
            if (string.CompareOrdinal(text, text.Length - len, LeakOpen, 0, len) == 0)
            {
                return len;
            }
        }

        return 0;
    }

    /// <summary>
    /// Settle the hold-back at stream end: an open leak segment is salvage-parsed,
    /// held normal text is emitted as-is (it was never an opener).
    /// </summary>
    private void DrainHeldContent(List<ChatModelChunk> chunks)
    {
        if (_heldText.Length == 0 && !_inLeak)
        {
            return;
        }

        var held = _heldText.ToString();
        _heldText.Clear();

        if (_inLeak)
        {
            _inLeak = false;
            SalvageSegment(held, chunks);
            return;
        }

        if (held.Length > 0)
        {
            chunks.Add(new ChatModelChunk { TextDelta = held });
        }
    }

    /// <summary>
    /// Parse one leaked tool-call segment (the text between the markers) into a real
    /// call. Two shapes are recognised: the Hermes JSON body
    /// (<c>{"name": "...", "arguments": {...}}</c>) and the qwen3-coder XML body
    /// (<c>&lt;function=name&gt;&lt;parameter=key&gt;value&lt;/parameter&gt;...&lt;/function&gt;</c>).
    /// Anything else - like the mangled tags a drifting model emits - is dropped and
    /// counted, never surfaced as text.
    /// </summary>
    private void SalvageSegment(string segment, List<ChatModelChunk> chunks)
    {
        var call = TrySalvageJson(segment) ?? TrySalvageXml(segment);
        if (call is null)
        {
            DroppedLeakChars += segment.Length + LeakOpen.Length;
            return;
        }

        SalvagedToolCalls++;
        chunks.Add(new ChatModelChunk { ToolCall = call });
    }

    private ChatModelToolCall? TrySalvageJson(string segment)
    {
        var body = segment.Trim();
        if (body.Length == 0 || body[0] != '{')
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                return null;
            }

            var arguments = "{}";
            if (doc.RootElement.TryGetProperty("arguments", out var args))
            {
                arguments = args.ValueKind switch
                {
                    JsonValueKind.Object => args.GetRawText(),
                    // Some models double-encode arguments as a JSON string.
                    JsonValueKind.String when LooksLikeJsonObject(args.GetString()) => args.GetString()!,
                    _ => "{}",
                };
            }

            return new ChatModelToolCall
            {
                Id = NextSalvageId(name.GetString()!),
                Name = name.GetString()!,
                ArgumentsJson = arguments,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ChatModelToolCall? TrySalvageXml(string segment)
    {
        var fn = System.Text.RegularExpressions.Regex.Match(
            segment,
            @"<function=([A-Za-z0-9_\-.]+)>",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(250));
        if (!fn.Success)
        {
            return null;
        }

        var arguments = new System.Text.Json.Nodes.JsonObject();
        var parameters = System.Text.RegularExpressions.Regex.Matches(
            segment,
            @"<parameter=([A-Za-z0-9_\-.]+)>\n?(.*?)\n?</parameter>",
            System.Text.RegularExpressions.RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(250));
        foreach (System.Text.RegularExpressions.Match p in parameters)
        {
            arguments[p.Groups[1].Value] = ParseParameterValue(p.Groups[2].Value);
        }

        return new ChatModelToolCall
        {
            Id = NextSalvageId(fn.Groups[1].Value),
            Name = fn.Groups[1].Value,
            ArgumentsJson = arguments.ToJsonString(),
        };
    }

    /// <summary>
    /// A parameter value keeps its JSON type when it parses as a JSON literal/array/
    /// object (the schema-typed values the template renders unquoted); otherwise it
    /// rides as a plain string.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode? ParseParameterValue(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '{' or '[' or 't' or 'f' or 'n' or '-' || char.IsDigit(trimmed[0])))
        {
            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // fall through to the string form
            }
        }

        return System.Text.Json.Nodes.JsonValue.Create(raw);
    }

    private static bool LooksLikeJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string NextSalvageId(string name) => $"salvaged_{name}_{_salvageCounter++}";

    private void BufferToolCallFragment(StreamingChatToolCallUpdate fragment, List<ChatModelChunk> chunks)
    {
        if (!_toolCalls.TryGetValue(fragment.Index, out var buffer))
        {
            buffer = new ToolCallBuffer();
            _toolCalls[fragment.Index] = buffer;
        }

        if (!string.IsNullOrEmpty(fragment.ToolCallId))
        {
            buffer.Id = fragment.ToolCallId;
        }

        if (!string.IsNullOrEmpty(fragment.FunctionName))
        {
            buffer.Name = fragment.FunctionName;

            // First time we learn the name: announce the call so the UI can show
            // intent while the arguments are still streaming. The id matches the
            // eventual flushed call (same `Id ?? call_<name>` fallback). Announce
            // once per call.
            if (!buffer.Announced && !string.IsNullOrEmpty(buffer.Name))
            {
                buffer.Announced = true;
                chunks.Add(new ChatModelChunk
                {
                    ToolCallStart = new ToolCallStart
                    {
                        Id = buffer.Id ?? $"call_{buffer.Name}",
                        Name = buffer.Name,
                    },
                });
            }
        }

        if (fragment.FunctionArgumentsUpdate is { } args)
        {
            var text = args.ToString();
            if (text.Length > 0)
            {
                buffer.Arguments.Append(text);
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
                    ArgumentsJson = NormalizeArguments(buffer.Arguments),
                },
            });
        }

        _toolCalls.Clear();
    }

    /// <summary>
    /// The accumulated arguments must be a parseable JSON object, or they degrade
    /// to <c>{}</c>. vLLM 0.22's qwen3 tool parser streams an unterminated
    /// <c>{</c> (and nothing else) for a no-argument call when thinking is
    /// disabled; passing that fragment through poisons the whole turn - the tool
    /// rejects it, and echoing it back inside the next round's assistant
    /// <c>tool_calls</c> message makes the server 400 on its own output
    /// (<c>from_json</c> in the chat template). <c>{}</c> preserves the call -
    /// truncated arguments become a readable tool error, never a dead turn.
    /// </summary>
    private static string NormalizeArguments(System.Text.StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return "{}";
        }

        var args = buffer.ToString();
        try
        {
            using var doc = JsonDocument.Parse(args);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? args : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
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

        /// <summary>True once the name has been announced as a <see cref="ToolCallStart"/>.</summary>
        public bool Announced { get; set; }

        public System.Text.StringBuilder Arguments { get; } = new();
    }
}
