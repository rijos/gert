using System.Diagnostics;
using System.Text.Json;
using Gert.Model.Agent;
using Gert.Model.Chat;
using Gert.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Gert.Agent.Loop;

/// <summary>
/// <see cref="IAgentLoop"/> -- the reusable tool loop (chat-and-tools.md section the tool loop) the
/// chat shell, the sub-agent, and a headless driver all run. <see cref="RunAsync"/> is the
/// orchestrator; the work splits into a per-run <see cref="Toolset"/> (tool view + effective bounds +
/// trackers), a <see cref="DeltaAccumulator"/> (the content/reasoning fold for the returned result and
/// the round-narration slice), <see cref="StreamRoundAsync"/> (consume one model stream), and
/// <see cref="ExecuteRoundAsync"/> (run the round's tool calls). Because nothing here <c>yield</c>s,
/// the model stream is consumed inside ordinary <c>try/catch</c>.
///
/// <para>
/// The model is a Microsoft.Extensions.AI <see cref="IChatClient"/> (decisions #13): the loop builds
/// a working <see cref="ChatMessage"/> list + a per-round <see cref="ChatOptions"/> and consumes the
/// <see cref="ChatResponseUpdate"/> stream, folding it into <see cref="AgentEvent"/>s. Sampling rides
/// the provider inside the client; the options carry only the advertised tools + the per-round token
/// cap. A streamed <see cref="FunctionCallContent"/> with null <see cref="FunctionCallContent.Arguments"/>
/// is a live name-first intent (the running card); a non-null arguments dictionary is a completed call.
/// </para>
///
/// <para>
/// The loop's only output is <see cref="AgentEvent"/>, emitted through the one
/// <see cref="IAgentEventSink"/>: it knows nothing of <c>IChatRepository</c> / <c>IConversationBus</c>
/// / coalescing / persistence. Text and reasoning ride as raw per-chunk deltas (the consumer
/// coalesces); tool started/completed, round, and finish are discrete. Stateless beyond the clock -
/// safe as a singleton.
/// </para>
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    private readonly TimeProvider _clock;
    private readonly ILogger<AgentLoop> _logger;

    public AgentLoop(TimeProvider clock, ILogger<AgentLoop> logger)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AgentResult> RunAsync(
        AgentLoopRequest request,
        IAgentEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        // The growing conversation sent upstream: the caller's initial messages (system + history)
        // copied into the loop's own working list, then the tool-call/tool-result pairs it appends.
        var messages = new List<ChatMessage>(request.Messages.Count + 4);
        messages.AddRange(request.Messages);

        var toolset = request.Tools;

        // The pure fold: builds Content/Reasoning for the returned result and gives each round the
        // mark to slice its own narration. Coalescing into durable rows is the consumer's job.
        var acc = new DeltaAccumulator();

        int? tokenCount = null;
        int? promptTokens = null;
        long genElapsedTicks = 0;
        var round = 0;

        while (true)
        {
            // Mark this round's start in the accumulated content so its narration can be sliced
            // out for the assistant tool-calls message (qwen narrates while it calls tools).
            var roundContentStart = acc.Length;

            var options = BuildOptions(request, toolset.AdvertisedSpecs);
            var draft = await StreamRoundAsync(request.Model, messages, options, toolset, acc, sink, cancellationToken)
                .ConfigureAwait(false);

            genElapsedTicks += draft.GenTicks;
            if (draft.TokenCount is not null)
            {
                tokenCount = draft.TokenCount;
            }

            if (draft.PromptTokens is not null)
            {
                // Last round wins - the largest prompt is the turn's real context footprint.
                promptTokens = draft.PromptTokens;
            }

            // No tool calls -> the model produced its final answer; leave the loop.
            if (draft.ToolCalls.Count == 0)
            {
                break;
            }

            // The wind-down brake decides whether this round runs at all (false = hard stop).
            if (!AdvanceRound(ref round, request.MaxRounds, toolset, draft, out var budgetExhausted))
            {
                break;
            }

            await ExecuteRoundAsync(
                draft, toolset, request, acc, sink, messages, roundContentStart, budgetExhausted, cancellationToken)
                .ConfigureAwait(false);

            // The tool boundary: a discrete beat the consumer maps to the streaming-row progress flush.
            await sink.EmitAsync(new RoundCompleted(round, tokenCount ?? 0), cancellationToken).ConfigureAwait(false);
        }

        var result = new AgentResult
        {
            Content = acc.Content,
            Reasoning = acc.Reasoning,
            TokenCount = tokenCount,
            PromptTokens = promptTokens,
            GenElapsedTicks = genElapsedTicks,
            ToolRounds = round,
        };

        await sink.EmitAsync(new TurnFinished(result), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Consume ONE model stream: pump text/reasoning deltas through the sink, emit the entitled
    /// tool-call-start card (live intent), collect the round's completed tool calls, and track the
    /// token counts + the pure generation span (stream consumption only - tool execution happens
    /// between rounds, outside this span). The accumulator folds each delta for the returned result +
    /// round narration.
    /// </summary>
    private async Task<RoundDraft> StreamRoundAsync(
        IChatClient model,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        Toolset toolset,
        DeltaAccumulator acc,
        IAgentEventSink sink,
        CancellationToken cancellationToken)
    {
        var toolCalls = new List<FunctionCallContent>();
        int? tokenCount = null;
        int? promptTokens = null;

        var roundStart = _clock.GetTimestamp();
        await foreach (var update in model.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text: { Length: > 0 } reasoning }:
                        {
                            var ev = new ReasoningDelta(reasoning);
                            acc.Apply(ev);
                            await sink.EmitAsync(ev, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                    case TextContent { Text: { Length: > 0 } text }:
                        {
                            var ev = new TextDelta(text);
                            acc.Apply(ev);
                            await sink.EmitAsync(ev, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                    // Null arguments = live intent: the model has named a tool but is still streaming
                    // its arguments. Emit a Running card NOW so the user sees what's coming (e.g.
                    // "Creating a file") instead of staring at the pulse. The full call + parsed
                    // request arrive at end-of-round below (same id -> the card updates in place). An
                    // unentitled call never announces: its card stays off-screen (the refusal is fed to
                    // the model below, not shown to the user).
                    case FunctionCallContent { Arguments: null } intent
                        when toolset.Resolve(intent.Name) is { Entitled: true } startEntry:
                        await sink.EmitAsync(new ToolStarted(intent.CallId, startEntry.Kind, null), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    // A completed call (non-null arguments dictionary, possibly empty).
                    case FunctionCallContent { Arguments: not null } call:
                        toolCalls.Add(call);
                        break;

                    case UsageContent usage:
                        if (usage.Details.OutputTokenCount is { } completion)
                        {
                            tokenCount = (int)completion;
                        }

                        if (usage.Details.InputTokenCount is { } prompt)
                        {
                            promptTokens = (int)prompt;
                        }

                        break;
                }
            }
        }

        var genTicks = _clock.GetElapsedTime(roundStart).Ticks;

        return new RoundDraft
        {
            ToolCalls = toolCalls,
            TokenCount = tokenCount,
            PromptTokens = promptTokens,
            GenTicks = genTicks,
        };
    }

    /// <summary>
    /// Apply the round cap. Past the budget the round's calls are NOT executed - each is refused with
    /// a synthetic error result instead (the wire format requires a result per call, and dropping the
    /// round whole would also drop the narration the model must see next round), tools stop being
    /// advertised, and the model gets ONE wind-down round to answer with what it has. (Clearing the
    /// tools array re-renders the templated system/tools region upstream and so invalidates the vLLM
    /// prefix cache for this final round - acceptable for a runaway loop.) If the wind-down round STILL
    /// emits tool calls - a tool-heavy history invites imitation even with nothing advertised - this
    /// returns false: stop calling upstream and finalise with what already streamed.
    /// <paramref name="budgetExhausted"/> is set so <see cref="ExecuteRoundAsync"/> refuses the round's
    /// calls.
    /// </summary>
    private bool AdvanceRound(
        ref int round,
        int maxRounds,
        Toolset toolset,
        RoundDraft draft,
        out bool budgetExhausted)
    {
        budgetExhausted = round >= maxRounds;
        if (budgetExhausted)
        {
            if (toolset.AdvertisedSpecs.Count == 0)
            {
                _logger.LogWarning(
                    "Wind-down round still produced {CallCount} tool call(s) - stopping upstream calls and finalising with the streamed content.",
                    draft.ToolCalls.Count);
                return false;
            }

            _logger.LogWarning(
                "Tool budget exhausted after {MaxToolRounds} rounds - refusing {CallCount} call(s) and winding the turn down.",
                maxRounds, draft.ToolCalls.Count);
            toolset.WindDown();
            return true;
        }

        round++;
        _logger.LogDebug(
            "Tool round {Round}/{MaxToolRounds}: {CallCount} call(s) ({ToolNames}).",
            round, maxRounds, draft.ToolCalls.Count, string.Join(", ", draft.ToolCalls.Select(c => c.Name)));
        return true;
    }

    /// <summary>
    /// Run one round's tool calls. The assistant tool_calls message goes first (history-order: ONE
    /// assistant message carrying the whole round's calls + this round's narration as content, so a
    /// model that narrates while it calls tools sees its own words next round), then per call -
    /// entitlement card gating, the per-tool call ceiling, execution under the effective bounds, the
    /// <see cref="ToolCompleted"/> event (the consumer renders the result card + persists the row +
    /// citations + artifacts), and the tool-role message.
    /// </summary>
    private async Task ExecuteRoundAsync(
        RoundDraft draft,
        Toolset toolset,
        AgentLoopRequest request,
        DeltaAccumulator acc,
        IAgentEventSink sink,
        List<ChatMessage> messages,
        int roundContentStart,
        bool budgetExhausted,
        CancellationToken cancellationToken)
    {
        // The text the model streamed THIS round rides along as content - a model that narrates while
        // it calls tools (qwen does) must see its own words next round, or it believes the work never
        // happened and restarts the answer ("oops, I jumped the gun"). A tool-call-only assistant turn
        // carries no TextContent (the adapter then omits `content`).
        var roundContent = acc.ContentSince(roundContentStart);
        var assistantContents = new List<AIContent>(draft.ToolCalls.Count + 1);
        if (roundContent.Length > 0)
        {
            assistantContents.Add(new TextContent(roundContent));
        }

        assistantContents.AddRange(draft.ToolCalls);
        messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

        foreach (var call in draft.ToolCalls)
        {
            // A user stop (or shutdown/timeout) mid-round: unwind the whole chain NOW rather than
            // running the round's remaining calls. The OCE lands in the driver's cancel finalize.
            cancellationToken.ThrowIfCancellationRequested();

            var entry = toolset.Resolve(call.Name);
            var argumentsJson = SerializeArguments(call.Arguments);

            // The plan-time ceiling decides visibility: an unentitled call (a tool the model was never
            // offered but emitted anyway) still gets a synthetic refusal in the upstream history, but
            // shows no card and writes no tool row - invisible live and on reload.
            var entitled = entry?.Entitled ?? false;
            var kind = entry?.Kind ?? call.Name;

            if (entitled)
            {
                await sink.EmitAsync(
                    new ToolStarted(call.CallId, kind, DisplayArguments(call.Arguments)),
                    cancellationToken).ConfigureAwait(false);
            }

            // The per-tool call budget refuses like the round budget: a synthetic failure the model
            // reads, never a torn turn.
            var outcome = budgetExhausted
                ? ToolOutcome.Failure(
                    kind,
                    $"tool budget exhausted ({request.MaxRounds} rounds) - no further tool calls will run this turn; answer with what you already have")
                : entry is not null && !toolset.TryConsumeCall(entry)
                    ? ToolOutcome.Failure(
                        kind,
                        $"tool '{entry.Tool.Id}' call budget exhausted ({entry.Effective.MaxCallsPerTurn} per turn) - no further '{entry.Tool.Name}' calls will run this turn; answer with what you already have")
                    : await ExecuteToolAsync(entry, call, argumentsJson, request, cancellationToken).ConfigureAwait(false);

            // The result card, the durable tool row, its citations, and any canvas artifacts are all
            // the entitled call's visible/persistent footprint, carried in ONE event the consumer
            // unpacks - skipped wholesale for an unentitled call (a refusal never produces
            // hits/artifacts anyway, and must leave no trace).
            if (entitled)
            {
                await sink.EmitAsync(
                    new ToolCompleted(new ExecutedToolCall
                    {
                        CallId = call.CallId,
                        Kind = outcome.Kind,
                        Status = outcome.Status,
                        RequestJson = argumentsJson,
                        ResponseJson = outcome.ResponseJson,
                        LatencyMs = outcome.LatencyMs,
                        Citations = outcome.Citations,
                        Artifacts = outcome.Artifacts,
                        Hits = outcome.Hits,
                        Stdout = outcome.Stdout,
                        Todos = outcome.Todos,
                        Error = outcome.Error,
                    }),
                    cancellationToken).ConfigureAwait(false);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, outcome.ResponseJson ?? string.Empty)]));
        }
    }

    /// <summary>
    /// Execute one tool call against its resolved <paramref name="entry"/>. The entitlement re-check
    /// runs against the run's plan-time snapshot - the claim is the ceiling at execution time too,
    /// even off-thread (auth.md). The host is wrapped in <see cref="BudgetedToolHost"/> carrying the
    /// tool's effective <c>TokenBudget</c>, and the call runs under its effective <c>CallTimeout</c>
    /// (Modal tools exempt; <c>&lt;= 0</c> disables).
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        ToolEntry? entry,
        FunctionCallContent call,
        string argumentsJson,
        AgentLoopRequest request,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return ToolOutcome.Failure("unknown", $"no tool named '{call.Name}'");
        }

        var tool = entry.Tool;

        // Defence-in-depth: never run a tool the user isn't entitled to, even if it somehow reached
        // the model (e.g. a poisoned history).
        if (!entry.Entitled)
        {
            return ToolOutcome.Failure(tool.Id, $"tool '{tool.Id}' is not permitted");
        }

        var invocation = new ToolInvocation
        {
            Pid = request.Pid,
            ArgumentsJson = argumentsJson,
            // The artifact tools key/persist canvas artifacts on the conversation.
            ConversationId = request.ConversationId,
            MessageId = request.MessageId,
            ToolCallId = call.CallId,
            Deadline = request.Host.Limits.Deadline,
            ClientTimezone = request.ClientTimezone,
            // The sub-agent's provider + nested-entitlement ceiling: delegation talks to the turn's
            // own model and can never out-tool the caller.
            ModelId = request.ModelId,
            AllowedToolIds = request.Tools.AllowedToolIds,
        };

        // Per-tool nested-work allowance: feed the existing (unconsumed) token-budget seam.
        var host = new BudgetedToolHost(request.Host, entry.Effective.TokenBudget);

        // The generic per-call backstop: tools carry their own tighter limits (sandbox wall clock,
        // search timeouts); this catches a hang outside them. A trip fails THIS call with a visible
        // card error - the turn token cancelling rethrows as before and ends the turn. Modal tools
        // (ask_user, sub_agent) are exempt - blocking/long-running IS their job; their own Deadline
        // math is the backstop and the turn lifetime token remains the hard wall.
        var timeout = entry.Effective.CallTimeout;
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero && tool.Type != ToolType.Modal)
        {
            callCts.CancelAfter(timeout);
        }

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(invocation, host, callCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && callCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Tool '{ToolId}' timed out after {TimeoutSeconds}s (call {CallId}).",
                tool.Id, timeout.TotalSeconds, call.CallId);
            return ToolOutcome.Failure(
                tool.Id,
                $"tool timed out after {timeout.TotalSeconds:0}s",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ToolOutcome.Failure(tool.Id, ex.Message, stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        return ToolOutcome.From(tool.Id, result, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Build the next completion's options: the currently advertised specs (as AIFunction declarations) + the per-round cap.</summary>
    private static ChatOptions BuildOptions(AgentLoopRequest request, IReadOnlyList<ChatToolSpec> specs) => new()
    {
        // Advertise-only declarations: the loop dispatches the ITool itself (Movement A), so these
        // carry no body. M.E.AI maps tool_choice:"auto" when tools are present and omits it when not.
        Tools = specs.Count > 0 ? specs.Select(ToAITool).ToList() : null,

        // The per-round completion cap is the only sampling field the request carries; the rest rides
        // the provider (inside the IChatClient).
        MaxOutputTokens = MaxTokensThisRound(request),
    };

    private static AITool ToAITool(ChatToolSpec spec) =>
        AIFunctionFactory.CreateDeclaration(spec.Name, spec.Description, ParseSchema(spec.ParametersSchema));

    /// <summary>
    /// Parse a tool's parameter-schema string into a <see cref="JsonElement"/>. A malformed/empty
    /// schema degrades to an empty object schema rather than throwing - a bad tool spec must not
    /// crash the whole turn.
    /// </summary>
    private static JsonElement ParseSchema(string schema)
    {
        if (!string.IsNullOrWhiteSpace(schema))
        {
            try
            {
                using var doc = JsonDocument.Parse(schema);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // fall through to the empty object schema
            }
        }

        return EmptyObjectSchema;
    }

    /// <summary>
    /// The per-round completion bound: <see cref="AgentLoopRequest.MaxTokensPerRound"/> when set
    /// (&gt; 0), else null - the provider's own default applies.
    /// </summary>
    private static int? MaxTokensThisRound(AgentLoopRequest request) =>
        request.MaxTokensPerRound is { } cap && cap > 0 ? cap : null;

    /// <summary>Serialize the model's parsed arguments back to JSON for the tool invocation + the row's request (compact; the values round-trip the model's bytes).</summary>
    private static string SerializeArguments(IDictionary<string, object?>? arguments) =>
        arguments is null ? "{}" : JsonSerializer.Serialize(arguments);

    /// <summary>
    /// Project the parsed arguments into the display map the running card shows. Request is
    /// display-only - long strings are capped so a whole-file argument (make_artifact content) doesn't
    /// bloat the event payload; the tool itself gets the full <c>ArgumentsJson</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? DisplayArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            map[key] = DisplayValue(value);
        }

        return map;
    }

    private static object? DisplayValue(object? value)
    {
        if (value is not JsonElement element)
        {
            return value is string s ? Cap(s) : value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => Cap(element.GetString()),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };

        static string? Cap(string? value) =>
            value is { Length: > 240 } ? value[..240] + "..." : value;
    }
}
