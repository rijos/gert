using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using OpenAIChatFinishReason = OpenAI.Chat.ChatFinishReason;

namespace Gert.Chat.OpenAI;

/// <summary>
/// Pure, network-free mapper from the OpenAI SDK's <see cref="StreamingChatCompletionUpdate"/>s
/// (one per SSE <c>data:</c> chunk on <c>/v1/chat/completions</c>) to Microsoft.Extensions.AI
/// <see cref="ChatResponseUpdate"/>s. The M.E.AI OpenAI adapter owns the SSE/JSON wire parsing
/// and produces those raw updates; this class re-parses each raw update to reproduce everything
/// the adapter does NOT (chat-and-tools.md section tool-call robustness; decisions #13) - it is the
/// engine of <see cref="SalvagingChatClient"/>.
///
/// <para>
/// <b>Why re-parse the raw update</b> instead of consuming the adapter's mapped contents: the
/// adapter (a) does not surface vLLM's <c>delta.reasoning</c> field (only the older
/// <c>reasoning_content</c>), (b) coalesces tool calls into one completed
/// <see cref="FunctionCallContent"/> emitted on a synthetic trailing update, so there is no
/// name-first live-intent signal and no chance to salvage leaked markup or normalise truncated
/// arguments, and (c) has no concept of the <c>&lt;tool_call&gt;</c> leak the qwen3-coder parser
/// drops into <c>content</c>. Reading the raw update gives the same inputs the old
/// pre-M.E.AI wire client had, so the salvage/reasoning/normalise behaviour is preserved verbatim.
/// </para>
///
/// <para>
/// <b>Output convention.</b> Each emitted update carries one piece: a text delta
/// (<see cref="TextContent"/>), a reasoning delta (<see cref="TextReasoningContent"/>), a live
/// tool-call intent (<see cref="FunctionCallContent"/> with <b>null Arguments</b> - the name is
/// known but the arguments are still streaming), a completed tool call
/// (<see cref="FunctionCallContent"/> with a non-null arguments dictionary, possibly empty), or the
/// terminal finish (<see cref="ChatResponseUpdate.FinishReason"/> + a
/// <see cref="UsageContent"/> when token counts are known). The consumer distinguishes the
/// live intent from the completed call by null-vs-non-null Arguments.
/// </para>
///
/// <para>
/// <b>Tool calls</b> arrive incrementally: the first delta for a tool call carries
/// <c>id</c> + <c>function.name</c>, later deltas append <c>function.arguments</c>
/// fragments, all keyed by <c>index</c>. We buffer per-index and only emit a completed call
/// when the stream finishes it (at the terminal chunk, a non-null <c>finish_reason</c>).
/// <b>Reasoning deltas</b> (vLLM extension): <c>--reasoning-parser</c> emits thinking as
/// <c>delta.reasoning</c> (vLLM >= 0.22) or <c>delta.reasoning_content</c> (older); both are
/// unknown to the OpenAI spec, so they are read off the raw update via <c>JsonPatch</c>.
/// <b>Leaked tool-call markup:</b> a recognised <c>&lt;tool_call&gt;</c> segment in content is
/// salvage-parsed into a completed call (Hermes JSON body or qwen3-coder XML body); an
/// unsalvageable segment is dropped, never shown. The counts are exposed for the client to log.
/// </para>
///
/// <para>Stateful - one instance per stream; not thread-safe.</para>
/// </summary>
internal sealed class OpenAIStreamParser
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
    /// Map one streamed update. Returns the updates to surface for it. The terminal
    /// finish update is only returned once a <c>finish_reason</c> arrives; usage may
    /// follow on a later update and is folded in.
    /// </summary>
    public IReadOnlyList<ChatResponseUpdate> Parse(StreamingChatCompletionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var updates = new List<ChatResponseUpdate>();

        // Usage may arrive on a trailing usage-only chunk (empty choices).
        if (update.Usage is { } usage)
        {
            _completionTokens = usage.OutputTokenCount;
            _promptTokens = usage.InputTokenCount;

            // vLLM sends the usage tail AFTER the finish_reason chunk, so the
            // finish update has already gone out without it - surface a trailing
            // usage update so the consumer still observes the counts.
            if (_finished)
            {
                updates.Add(Usage());
            }
        }

        // Reasoning ("thinking") delta - emitted by --reasoning-parser before the answer's content
        // deltas. vLLM exposes it as either `reasoning` or `reasoning_content` depending on the
        // server build; accept both. Spec-unknown fields ride the Patch (SCME0001 is NoWarn'd
        // project-wide; see the csproj note).
        if (update.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? thought)
            || update.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out thought))
        {
            if (!string.IsNullOrEmpty(thought))
            {
                updates.Add(Reasoning(thought));
            }
        }

        // Content deltas - routed through the leak hold-back (see class doc).
        foreach (var part in update.ContentUpdate)
        {
            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
            {
                AppendContent(part.Text, updates);
            }
        }

        foreach (var fragment in update.ToolCallUpdates)
        {
            BufferToolCallFragment(fragment, updates);
        }

        if (update.FinishReason is { } finishReason)
        {
            _finishReason = MapFinishReason(finishReason);

            // Settle the hold-back first: an open leak segment salvages (or drops)
            // and held normal text flushes - its updates must precede the finish.
            DrainHeldContent(updates);

            // Flush buffered tool calls, then the finish update. Usage may still arrive
            // on a later usage-only chunk; vLLM sends the usage tail AFTER the
            // finish_reason chunk, so we cannot wait for it here. We emit finish now
            // and accept that token count is best-effort (folded if it was already seen).
            FlushToolCalls(updates);
            EmitFinish(updates);
        }

        return updates;
    }

    /// <summary>
    /// Flush any buffered tool calls + the terminal finish update. Call once after the
    /// stream ends to surface a finish update even if the server omitted a usage tail.
    /// Idempotent: returns nothing if the terminal update was already emitted inline.
    /// </summary>
    public IReadOnlyList<ChatResponseUpdate> Flush()
    {
        if (_finished)
        {
            return [];
        }

        var updates = new List<ChatResponseUpdate>();
        DrainHeldContent(updates);
        FlushToolCalls(updates);
        EmitFinish(updates);
        return updates;
    }

    /// <summary>Map the SDK's finish reason to the wire's snake_case string.</summary>
    private static string MapFinishReason(OpenAIChatFinishReason reason) => reason switch
    {
        OpenAIChatFinishReason.Stop => "stop",
        OpenAIChatFinishReason.ToolCalls => "tool_calls",
        OpenAIChatFinishReason.Length => "length",
        OpenAIChatFinishReason.ContentFilter => "content_filter",
        OpenAIChatFinishReason.FunctionCall => "function_call",
        _ => reason.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Route a content delta through the leak hold-back. In normal state, text streams
    /// through minus the longest tail that could still open a <c>&lt;tool_call&gt;</c>;
    /// once an opener completes, the segment buffers silently until its close tag (or
    /// stream end) and is salvage-parsed there.
    /// </summary>
    private void AppendContent(string text, List<ChatResponseUpdate> updates)
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
                        updates.Add(Text(LeakOpen + held));
                    }

                    return;
                }

                SalvageSegment(held[..close], updates);
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
                    updates.Add(Text(held[..open]));
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
                updates.Add(Text(held[..^hold]));
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
    private void DrainHeldContent(List<ChatResponseUpdate> updates)
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
            SalvageSegment(held, updates);
            return;
        }

        if (held.Length > 0)
        {
            updates.Add(Text(held));
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
    private void SalvageSegment(string segment, List<ChatResponseUpdate> updates)
    {
        var call = TrySalvageJson(segment) ?? TrySalvageXml(segment);
        if (call is null)
        {
            DroppedLeakChars += segment.Length + LeakOpen.Length;
            return;
        }

        SalvagedToolCalls++;
        updates.Add(Call(call.Value.Id, call.Value.Name, call.Value.ArgumentsJson));
    }

    private (string Id, string Name, string ArgumentsJson)? TrySalvageJson(string segment)
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

            return (NextSalvageId(name.GetString()!), name.GetString()!, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private (string Id, string Name, string ArgumentsJson)? TrySalvageXml(string segment)
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

        return (NextSalvageId(fn.Groups[1].Value), fn.Groups[1].Value, arguments.ToJsonString());
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

    private void BufferToolCallFragment(StreamingChatToolCallUpdate fragment, List<ChatResponseUpdate> updates)
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

            // First time we learn the name: announce the call (null-args FunctionCallContent) so the
            // UI can show intent while the arguments are still streaming. The id matches the
            // eventual completed call (same `Id ?? call_<name>` fallback). Announce once per call.
            if (!buffer.Announced && !string.IsNullOrEmpty(buffer.Name))
            {
                buffer.Announced = true;
                updates.Add(Intent(buffer.Id ?? $"call_{buffer.Name}", buffer.Name));
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

    private void FlushToolCalls(List<ChatResponseUpdate> updates)
    {
        foreach (var buffer in _toolCalls.Values)
        {
            if (string.IsNullOrEmpty(buffer.Name))
            {
                continue;
            }

            updates.Add(Call(buffer.Id ?? $"call_{buffer.Name}", buffer.Name, NormalizeArguments(buffer.Arguments)));
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

    private void EmitFinish(List<ChatResponseUpdate> updates)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        var contents = new List<AIContent>();
        if (_completionTokens is not null || _promptTokens is not null)
        {
            contents.Add(UsageDetail());
        }

        updates.Add(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = ToFinishReason(_finishReason ?? "stop"),
            Contents = contents,
        });
    }

    private ChatResponseUpdate Usage() => new()
    {
        Role = ChatRole.Assistant,
        Contents = [UsageDetail()],
    };

    private UsageContent UsageDetail() => new(new UsageDetails
    {
        InputTokenCount = _promptTokens,
        OutputTokenCount = _completionTokens,
    });

    private static ChatResponseUpdate Text(string text) =>
        new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Reasoning(string text) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new TextReasoningContent(text)],
    };

    private static ChatResponseUpdate Intent(string id, string name) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name)],
    };

    private static ChatResponseUpdate Call(string id, string name, string argumentsJson) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name, ParseArguments(argumentsJson))],
    };

    /// <summary>
    /// Parse a normalised JSON-object argument string into the arguments dictionary M.E.AI carries.
    /// Values stay as <see cref="JsonElement"/> so re-serialisation (the tool's request JSON, and
    /// the next round's echoed assistant message) round-trips the model's bytes faithfully. A
    /// non-null result (even an empty dictionary) marks a COMPLETED call, distinct from the
    /// null-Arguments live intent.
    /// </summary>
    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static AIChatFinishReason ToFinishReason(string reason) => reason switch
    {
        "stop" => AIChatFinishReason.Stop,
        "tool_calls" => AIChatFinishReason.ToolCalls,
        "length" => AIChatFinishReason.Length,
        "content_filter" => AIChatFinishReason.ContentFilter,
        _ => new AIChatFinishReason(reason),
    };

    /// <summary>Per-index accumulator for a streamed tool call.</summary>
    private sealed class ToolCallBuffer
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        /// <summary>True once the name has been announced as a live intent.</summary>
        public bool Announced { get; set; }

        public System.Text.StringBuilder Arguments { get; } = new();
    }
}
