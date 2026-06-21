using System.Diagnostics;
using System.Text.Json;
using Gert.Chat;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Tools;
using Microsoft.Extensions.Logging;

namespace Gert.Agent.Loop;

/// <summary>
/// <see cref="IAgentLoop"/> -- the reusable tool loop (chat-and-tools.md section the tool loop) the
/// chat shell, the sub-agent, and a headless driver all run. <see cref="RunAsync"/> is the
/// orchestrator; the work splits into a per-run <see cref="Toolset"/> (tool view + effective bounds +
/// trackers), a <see cref="DeltaSink"/> (the emit channel + coalescing + accumulators),
/// <see cref="StreamRoundAsync"/> (consume one model stream), and <see cref="ExecuteRoundAsync"/> (run
/// the round's tool calls). Because nothing here <c>yield</c>s, the model stream is consumed inside
/// ordinary <c>try/catch</c>.
///
/// <para>
/// The loop knows nothing of <c>IChatRepository</c> / <c>IConversationBus</c>: it talks only through
/// the request's <see cref="DeltaSink.Emit"/> (the in-loop events AND the seam tools emit through),
/// <see cref="AgentLoopRequest.OnToolExecuted"/> (the driver persists the tool_call row + collects
/// citations), and <see cref="AgentLoopRequest.OnProgress"/> (the streaming-row flush), plus the host
/// and the model client. Stateless beyond the clock - safe as a singleton.
/// </para>
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentLoop> _logger;

    public AgentLoop(TimeProvider clock, ILogger<AgentLoop> logger)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AgentLoopResult> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The growing conversation sent upstream: the caller's initial messages (system + history)
        // copied into the loop's own working list, then the tool-call/tool-result pairs it appends.
        var messages = new List<ChatModelMessage>(request.Messages.Count + 4);
        messages.AddRange(request.Messages);

        var toolset = request.Tools;
        var sink = new DeltaSink(request, _clock);

        int? tokenCount = null;
        int? promptTokens = null;
        long genElapsedTicks = 0;
        var round = 0;

        try
        {
            while (true)
            {
                // Mark this round's start in the accumulated content so its narration can be sliced
                // out for the assistant tool-calls message (qwen narrates while it calls tools).
                var roundContentStart = sink.Length;

                var completion = NewCompletion(request, messages, toolset.AdvertisedSpecs);
                var draft = await StreamRoundAsync(request.Model, completion, toolset, sink, cancellationToken)
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
                    draft, toolset, request, sink, messages, roundContentStart, budgetExhausted, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await sink.FlushTails(cancellationToken).ConfigureAwait(false);
        }

        return new AgentLoopResult
        {
            Content = sink.Content,
            Reasoning = sink.Reasoning,
            TokenCount = tokenCount,
            PromptTokens = promptTokens,
            GenElapsedTicks = genElapsedTicks,
            ToolRounds = round,
        };
    }

    /// <summary>
    /// Consume ONE model stream into <paramref name="sink"/>: pump text/reasoning deltas, emit the
    /// entitled tool-call-start card (live intent), collect the round's tool calls, and track the
    /// token counts + the pure generation span (stream consumption only - tool execution happens
    /// between rounds, outside this span).
    /// </summary>
    private async Task<RoundDraft> StreamRoundAsync(
        IChatModelClient model,
        ChatCompletionRequest completion,
        Toolset toolset,
        DeltaSink sink,
        CancellationToken cancellationToken)
    {
        var toolCalls = new List<ChatModelToolCall>();
        int? tokenCount = null;
        int? promptTokens = null;

        var roundStart = _clock.GetTimestamp();
        await foreach (var chunk in model.StreamAsync(completion, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
            {
                await sink.AppendReasoning(chunk.ReasoningDelta, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                await sink.AppendText(chunk.TextDelta, cancellationToken).ConfigureAwait(false);
            }

            // Live intent: the model has named a tool but is still streaming its arguments. Flush
            // streamed text first so the card lands after it, then emit a Running card NOW so the user
            // sees what's coming (e.g. "Creating a file") instead of staring at the pulse while a
            // whole-file argument streams. The full call + its parsed request arrive at end-of-round
            // below (same id -> the card updates in place). An unentitled call never announces: its
            // card stays off-screen (the refusal is fed to the model below, not shown to the user).
            if (chunk.ToolCallStart is { } toolStart && toolset.Resolve(toolStart.Name) is { Entitled: true } startEntry)
            {
                await sink.FlushBoundary(cancellationToken).ConfigureAwait(false);
                await sink.Emit(
                    new ToolCallEvent
                    {
                        Id = toolStart.Id,
                        Kind = startEntry.Kind,
                        Status = ToolCallStatus.Running,
                        Request = null,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (chunk.ToolCall is not null)
            {
                toolCalls.Add(chunk.ToolCall);
            }

            if (chunk.TokenCount is not null)
            {
                tokenCount = chunk.TokenCount;
            }

            if (chunk.PromptTokenCount is not null)
            {
                promptTokens = chunk.PromptTokenCount;
            }
        }

        var genTicks = _clock.GetElapsedTime(roundStart).Ticks;

        // Boundary flush: all of a round's text precedes its tool events, and the final round's text
        // precedes citations/message_end.
        await sink.FlushBoundary(cancellationToken).ConfigureAwait(false);

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
    /// prefix cache for this final round - acceptable for a runaway loop; tool_choice:"none" would
    /// preserve the prefix if vLLM support is ever confirmed.) If the wind-down round STILL emits tool
    /// calls - a tool-heavy history invites imitation even with nothing advertised - this returns
    /// false: stop calling upstream and finalise with what already streamed. <paramref name="budgetExhausted"/>
    /// is set so <see cref="ExecuteRoundAsync"/> refuses the round's calls.
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
    /// result card + persist + artifacts, and the tool-role message.
    /// </summary>
    private async Task ExecuteRoundAsync(
        RoundDraft draft,
        Toolset toolset,
        AgentLoopRequest request,
        DeltaSink sink,
        List<ChatModelMessage> messages,
        int roundContentStart,
        bool budgetExhausted,
        CancellationToken cancellationToken)
    {
        // The text the model streamed THIS round rides along as content - a model that narrates while
        // it calls tools (qwen does) must see its own words next round, or it believes the work never
        // happened and restarts the answer ("oops, I jumped the gun"). Content stays null otherwise.
        var roundContent = sink.ContentSince(roundContentStart);
        messages.Add(new ChatModelMessage
        {
            Role = "assistant",
            Content = roundContent.Length > 0 ? roundContent : null,
            ToolCalls = draft.ToolCalls,
        });

        foreach (var call in draft.ToolCalls)
        {
            // A user stop (or shutdown/timeout) mid-round: unwind the whole chain NOW rather than
            // running the round's remaining calls. The OCE lands in the driver's cancel finalize.
            cancellationToken.ThrowIfCancellationRequested();

            var entry = toolset.Resolve(call.Name);

            // The plan-time ceiling decides visibility: an unentitled call (a tool the model was never
            // offered but emitted anyway) still gets a synthetic refusal in the upstream history, but
            // shows no card and writes no tool row - invisible live and on reload.
            var entitled = entry?.Entitled ?? false;
            var kind = entry?.Kind ?? call.Name;

            if (entitled)
            {
                await sink.Emit(
                    new ToolCallEvent
                    {
                        Id = call.Id,
                        Kind = kind,
                        Status = ToolCallStatus.Running,
                        Request = ParseArgs(call.ArgumentsJson),
                    },
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
                    : await ExecuteToolAsync(entry, call, request, cancellationToken).ConfigureAwait(false);

            // The result card, the durable tool row, its citations, and any canvas artifacts are all
            // the entitled call's visible/persistent footprint - skipped wholesale for an unentitled
            // call (a refusal never produces hits/artifacts anyway, and must leave no trace).
            if (entitled)
            {
                await sink.Emit(
                    new ToolResultEvent
                    {
                        Id = call.Id,
                        Kind = outcome.Kind,
                        Status = outcome.Status,
                        LatencyMs = outcome.LatencyMs,
                        Hits = outcome.Hits,
                        Stdout = outcome.Stdout,
                        Todos = outcome.Todos,
                        Error = outcome.Error,
                    },
                    cancellationToken).ConfigureAwait(false);

                // Tool rows persist LIVE (the tree read model grows as the turn runs), and citations
                // keep their provenance: which call made them. The driver owns the row id so it can
                // bind citations to it.
                if (request.OnToolExecuted is { } onToolExecuted)
                {
                    await onToolExecuted(
                        new ExecutedToolCall
                        {
                            CallId = call.Id,
                            Kind = outcome.Kind,
                            Status = outcome.Status,
                            RequestJson = call.ArgumentsJson,
                            ResponseJson = outcome.ResponseJson,
                            LatencyMs = outcome.LatencyMs,
                            Citations = outcome.Citations,
                            Artifacts = outcome.Artifacts,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                // Canvas artifacts the call created/updated (make/edit tools): the tool already
                // persisted them; emit one ArtifactEvent each so the live canvas opens/updates. An
                // existing id updates the tab in place.
                if (outcome.Artifacts is { Count: > 0 } artifacts)
                {
                    foreach (var artifact in artifacts)
                    {
                        await sink.Emit(
                            new ArtifactEvent
                            {
                                Id = artifact.Id,
                                Kind = artifact.Kind,
                                Name = artifact.Name,
                                Content = artifact.Content,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            messages.Add(new ChatModelMessage
            {
                Role = "tool",
                Content = outcome.ResponseJson ?? string.Empty,
                ToolCallId = call.Id,
            });
        }

        // Tool boundary: flush accumulated text so thread reads see progress.
        if (request.OnProgress is { } onProgress)
        {
            await onProgress(sink.Content, cancellationToken).ConfigureAwait(false);
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
        ChatModelToolCall call,
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
            ArgumentsJson = call.ArgumentsJson,
            // The artifact tools key/persist canvas artifacts on the conversation.
            ConversationId = request.ConversationId,
            MessageId = request.MessageId,
            ToolCallId = call.Id,
            // The mid-execution emit seam (ask_user's question_asked): the driver's own
            // persist-then-publish protocol, so a tool-emitted event is durable before it is live and
            // replays like any other. No per-tool branch here - any tool may emit; null on an
            // autonomous driver (the tool fails closed rather than waiting invisibly).
            EmitAsync = request.Emit,
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
                tool.Id, timeout.TotalSeconds, call.Id);
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

    /// <summary>Build the next completion request: the working messages + the currently advertised specs + the per-round cap.</summary>
    private static ChatCompletionRequest NewCompletion(
        AgentLoopRequest request,
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatToolSpec> specs) =>
        new()
        {
            ModelId = request.ModelId,
            Messages = messages,
            Tools = specs,
            // The per-round completion cap is the only sampling field the request carries; the rest
            // rides the provider (Gert:Chat:Providers).
            MaxTokens = MaxTokensThisRound(request),
        };

    /// <summary>
    /// The per-round completion bound: <see cref="AgentLoopRequest.MaxTokensPerRound"/> when set
    /// (&gt; 0), else null - the provider's own default applies.
    /// </summary>
    private static int? MaxTokensThisRound(AgentLoopRequest request) =>
        request.MaxTokensPerRound is { } cap && cap > 0 ? cap : null;

    private static IReadOnlyDictionary<string, object?>? ParseArgs(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    // Request is display-only (the tool card) - cap long strings so a whole-file
                    // argument (make_artifact content) doesn't bloat the event payload; the tool
                    // itself gets the full ArgumentsJson.
                    JsonValueKind.String => Cap(prop.Value.GetString()),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Cap(string? value) =>
            value is { Length: > 240 } ? value[..240] + "..." : value;
    }
}
