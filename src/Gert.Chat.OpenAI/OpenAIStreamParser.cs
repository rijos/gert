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
/// and produces those raw updates; this class re-parses each raw update to reproduce the two things
/// the adapter does NOT (decisions #13).
///
/// <para>
/// <b>Why re-parse the raw update</b> instead of consuming the adapter's mapped contents: the
/// adapter (a) surfaces only the older <c>reasoning_content</c>, not vLLM's <c>delta.reasoning</c>;
/// and (b) coalesces a tool call into one completed <see cref="FunctionCallContent"/> emitted on a
/// synthetic trailing update, so there is no name-first live-intent signal (the running card). Reading
/// the raw update gives both. (The <c>&lt;tool_call&gt;</c> leak salvage and the truncated-argument
/// degrade-to-<c>{}</c> guard that this class used to carry are gone - the fixed vLLM qwen template no
/// longer leaks tool-call markup into <c>content</c> nor streams the unterminated-<c>{</c> fragment.)
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
/// </para>
///
/// <para>Stateful - one instance per stream; not thread-safe.</para>
/// </summary>
internal sealed class OpenAIStreamParser
{
    // Buffered tool calls keyed by their streamed index, in arrival order.
    private readonly SortedDictionary<int, ToolCallBuffer> _toolCalls = new();

    private string? _finishReason;
    private int? _completionTokens;
    private int? _promptTokens;
    private bool _finished;

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

        // Content deltas stream straight through (the fixed template no longer leaks tool-call markup
        // into content, so there is no hold-back to disambiguate).
        foreach (var part in update.ContentUpdate)
        {
            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
            {
                updates.Add(Text(part.Text));
            }
        }

        foreach (var fragment in update.ToolCallUpdates)
        {
            BufferToolCallFragment(fragment, updates);
        }

        if (update.FinishReason is { } finishReason)
        {
            _finishReason = MapFinishReason(finishReason);

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

            // A no-argument call streams an empty buffer; everything else rides verbatim (the fixed
            // template emits well-formed JSON, so there is no longer a degrade-to-{} guard here).
            var arguments = buffer.Arguments.Length == 0 ? "{}" : buffer.Arguments.ToString();
            updates.Add(Call(buffer.Id ?? $"call_{buffer.Name}", buffer.Name, arguments));
        }

        _toolCalls.Clear();
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
    /// Parse a JSON-object argument string into the arguments dictionary M.E.AI carries. Values stay
    /// as <see cref="JsonElement"/> so re-serialisation (the tool's request JSON, and the next round's
    /// echoed assistant message) round-trips the model's bytes faithfully. A non-null result (even an
    /// empty dictionary) marks a COMPLETED call, distinct from the null-Arguments live intent;
    /// unparseable bytes fall back to an empty object rather than tearing the turn.
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
