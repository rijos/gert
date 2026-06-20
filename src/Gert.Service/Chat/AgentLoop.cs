using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Tools;
using Microsoft.Extensions.Logging;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="IAgentLoop"/> -- the reusable tool loop (chat-and-tools.md section the
/// tool loop), extracted verbatim from the old <c>TurnRunner</c> so the chat shell,
/// the sub-agent, and a headless driver run the same body. Because nothing here
/// <c>yield</c>s, the model stream is consumed inside ordinary <c>try/catch</c>.
/// Text deltas coalesce into one event per <see cref="AgentLoopRequest.DeltaFlushInterval"/>
/// window (size-capped by <see cref="AgentLoopRequest.DeltaFlushMaxChars"/>) - the
/// window opens at stream start, so after a typical prefill the first token flushes
/// immediately and time-to-first-token stays the model's, not the turn's.
///
/// <para>
/// The loop knows nothing of <c>IChatRepository</c> / <c>IConversationBus</c>: it
/// talks only through the request's <see cref="AgentLoopRequest.Emit"/> (the in-loop
/// events AND the seam tools emit through), <see cref="AgentLoopRequest.OnToolExecuted"/>
/// (the driver persists the tool_call row + collects citations), and
/// <see cref="AgentLoopRequest.OnProgress"/> (the streaming-row flush), plus the host
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

        var content = new StringBuilder();
        var reasoning = new StringBuilder();

        // The growing conversation sent upstream: the caller's initial messages
        // (system + history) copied into the loop's own working list, then the
        // tool-call/tool-result pairs the loop appends.
        var messages = new List<ChatModelMessage>(request.Messages.Count + 4);
        messages.AddRange(request.Messages);

        var toolSpecs = request.ToolSpecs;
        int? tokenCount = null;
        int? promptTokens = null;
        long genElapsedTicks = 0;
        var round = 0;
        var searchCalls = 0; // consumed web_search budget (MaxSearchCallsPerTurn)

        // Delta coalescing: chunks buffer in `pending` (answer text) and
        // `pendingReasoning` (thinking text) and flush as ONE event each (one
        // seq = one durable row = one publish) on the time/size thresholds and
        // at every boundary. The splice stays exact - the streamer dedups by
        // seq, not by token granularity - while turn_events write amplification
        // drops by an order of magnitude. `content`/`reasoning` accumulate
        // per-chunk independently, so finalize paths never depend on a flush.
        // Reasoning always precedes content within a round, so its buffer
        // flushes first at every boundary to preserve wire ordering.
        var pending = new StringBuilder();
        var pendingReasoning = new StringBuilder();
        var lastFlushTs = _clock.GetTimestamp();
        var lastReasoningFlushTs = _clock.GetTimestamp();

        async Task EmitAsync(ChatEvent chatEvent)
        {
            if (request.Emit is { } emit)
            {
                await emit(chatEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        async Task FlushPendingReasoningAsync()
        {
            if (pendingReasoning.Length == 0)
            {
                return;
            }

            var text = pendingReasoning.ToString();
            pendingReasoning.Clear();
            lastReasoningFlushTs = _clock.GetTimestamp();
            await EmitAsync(new ReasoningEvent { Text = text }).ConfigureAwait(false);
        }

        async Task FlushPendingAsync()
        {
            await FlushPendingReasoningAsync().ConfigureAwait(false);

            if (pending.Length == 0)
            {
                return;
            }

            var text = pending.ToString();
            pending.Clear();
            lastFlushTs = _clock.GetTimestamp();
            await EmitAsync(new DeltaEvent { Text = text }).ConfigureAwait(false);
        }

        var model = request.Model;

        try
        {
            while (true)
            {
                var completion = new ChatCompletionRequest
                {
                    ModelId = request.ModelId,
                    Messages = messages,
                    Tools = toolSpecs,
                    // The per-round completion cap is the only sampling field the
                    // request carries; the rest rides the provider (Gert:Chat:Providers).
                    MaxTokens = MaxTokensThisRound(request),
                };

                var toolCalls = new List<ChatModelToolCall>();
                var roundContentStart = content.Length;

                // Pure generation time: spans cover stream consumption only -
                // tool execution happens between rounds, outside this span.
                var roundStart = _clock.GetTimestamp();
                await foreach (var chunk in model.StreamAsync(completion, cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                    {
                        reasoning.Append(chunk.ReasoningDelta);
                        pendingReasoning.Append(chunk.ReasoningDelta);
                        request.OnReasoning?.Invoke(chunk.ReasoningDelta);

                        var dueReasoning = request.DeltaFlushInterval <= TimeSpan.Zero
                            || pendingReasoning.Length >= request.DeltaFlushMaxChars
                            || _clock.GetElapsedTime(lastReasoningFlushTs) >= request.DeltaFlushInterval;
                        if (dueReasoning)
                        {
                            await FlushPendingReasoningAsync().ConfigureAwait(false);
                        }
                    }

                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                    {
                        content.Append(chunk.TextDelta);
                        pending.Append(chunk.TextDelta);
                        request.OnText?.Invoke(chunk.TextDelta);

                        var due = request.DeltaFlushInterval <= TimeSpan.Zero
                            || pending.Length >= request.DeltaFlushMaxChars
                            || _clock.GetElapsedTime(lastFlushTs) >= request.DeltaFlushInterval;
                        if (due)
                        {
                            await FlushPendingAsync().ConfigureAwait(false);
                        }
                    }

                    // Live intent: the model has named a tool but is still streaming
                    // its arguments. Flush streamed text first so the card lands after
                    // it, then emit a Running card NOW so the user sees what's coming
                    // (e.g. "Creating a file") instead of staring at the pulse while a
                    // whole-file argument streams. The full call + its parsed request
                    // arrive at end-of-round below (same id -> the card updates in place).
                    // An unentitled call never announces: its card stays off-screen
                    // (the refusal is fed to the model below, not shown to the user).
                    if (chunk.ToolCallStart is { } toolStart && IsEntitledCall(request, toolStart.Name))
                    {
                        await FlushPendingReasoningAsync().ConfigureAwait(false);
                        await FlushPendingAsync().ConfigureAwait(false);
                        await EmitAsync(new ToolCallEvent
                        {
                            Id = toolStart.Id,
                            Kind = ResolveKind(request, toolStart.Name),
                            Status = ToolCallStatus.Running,
                            Request = null,
                        }).ConfigureAwait(false);
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
                        // Last round wins - the largest prompt is the turn's
                        // real context footprint.
                        promptTokens = chunk.PromptTokenCount;
                    }
                }

                genElapsedTicks += _clock.GetElapsedTime(roundStart).Ticks;

                // Boundary flush: all of a round's text precedes its tool events,
                // and the final round's text precedes citations/message_end.
                await FlushPendingAsync().ConfigureAwait(false);

                // No tool calls -> the model produced its final answer; leave the loop.
                if (toolCalls.Count == 0)
                {
                    break;
                }

                // Round cap: past the budget the round's calls are NOT executed -
                // each is refused with a synthetic error result instead (the wire
                // format requires a result per call, and dropping the round whole
                // would also drop the narration the model must see next round),
                // tools stop being advertised, and the model gets ONE wind-down
                // round to answer with what it has. (Clearing the tools array
                // re-renders the templated system/tools region upstream and so
                // invalidates the vLLM prefix cache for this final round -
                // acceptable for a runaway loop; tool_choice:"none" would preserve
                // the prefix if vLLM support is ever confirmed.) If the wind-down
                // round STILL emits tool calls - a tool-heavy history invites
                // imitation even with nothing advertised - stop calling upstream
                // and finalise with what already streamed: before this brake the
                // loop spun against vLLM until MaxTurnDuration, 409-blocking the
                // conversation the whole time.
                var budgetExhausted = round >= request.MaxRounds;
                if (budgetExhausted)
                {
                    if (toolSpecs.Count == 0)
                    {
                        _logger.LogWarning(
                            "Wind-down round still produced {CallCount} tool call(s) - stopping upstream calls and finalising with the streamed content.",
                            toolCalls.Count);
                        break;
                    }

                    _logger.LogWarning(
                        "Tool budget exhausted after {MaxToolRounds} rounds - refusing {CallCount} call(s) and winding the turn down.",
                        request.MaxRounds, toolCalls.Count);
                    toolSpecs = [];
                }
                else
                {
                    round++;
                    _logger.LogDebug(
                        "Tool round {Round}/{MaxToolRounds}: {CallCount} call(s) ({ToolNames}).",
                        round, request.MaxRounds, toolCalls.Count,
                        string.Join(", ", toolCalls.Select(c => c.Name)));
                }

                // The assistant turn that asked for the tools must precede their
                // results in the upstream history: ONE assistant message carrying the
                // whole round's tool_calls (OpenAI wire format), then one tool-role
                // result message per call, in call order. The text the model streamed
                // THIS round rides along as content - a model that narrates while it
                // calls tools (qwen does) must see its own words next round, or it
                // believes the work never happened and restarts the answer ("oops, I
                // jumped the gun"). Content stays null for a narration-free round.
                var roundContent = content.ToString(roundContentStart, content.Length - roundContentStart);
                messages.Add(new ChatModelMessage
                {
                    Role = "assistant",
                    Content = roundContent.Length > 0 ? roundContent : null,
                    ToolCalls = toolCalls,
                });

                foreach (var call in toolCalls)
                {
                    // A user stop (or shutdown/timeout) mid-round: unwind the whole
                    // chain NOW rather than running the round's remaining calls. The
                    // OCE lands in the driver's cancel finalize like any other.
                    cancellationToken.ThrowIfCancellationRequested();

                    // The plan-time ceiling decides visibility: an unentitled call
                    // (a tool the model was never offered but emitted anyway) still
                    // gets a synthetic refusal in the upstream history, but shows no
                    // card and writes no tool row - invisible live and on reload.
                    var entitled = IsEntitledCall(request, call.Name);

                    if (entitled)
                    {
                        await EmitAsync(new ToolCallEvent
                        {
                            Id = call.Id,
                            Kind = ResolveKind(request, call.Name),
                            Status = ToolCallStatus.Running,
                            Request = ParseArgs(call.ArgumentsJson),
                        }).ConfigureAwait(false);
                    }

                    // The per-turn search budget refuses like the round budget:
                    // a synthetic failure the model reads, never a torn turn.
                    var outcome = budgetExhausted
                        ? ToolOutcome.Failure(
                            ResolveKind(request, call.Name),
                            $"tool budget exhausted ({request.MaxRounds} rounds) - no further tool calls will run this turn; answer with what you already have")
                        : !TryConsumeSearchBudget(request, call.Name, ref searchCalls)
                            ? ToolOutcome.Failure(
                                ResolveKind(request, call.Name),
                                $"web search budget exhausted ({request.MaxSearchCallsPerTurn} per turn) - no further searches will run this turn; answer with what you already found")
                            : await ExecuteToolAsync(request, call, cancellationToken).ConfigureAwait(false);

                    // The result card, the durable tool row, its citations, and any
                    // canvas artifacts are all the entitled call's visible/persistent
                    // footprint - skipped wholesale for an unentitled call (a refusal
                    // never produces hits/artifacts anyway, and must leave no trace).
                    if (entitled)
                    {
                        await EmitAsync(new ToolResultEvent
                        {
                            Id = call.Id,
                            Kind = outcome.Kind,
                            Status = outcome.Status,
                            LatencyMs = outcome.LatencyMs,
                            Hits = outcome.Hits,
                            Stdout = outcome.Stdout,
                            Todos = outcome.Todos,
                            Error = outcome.Error,
                        }).ConfigureAwait(false);

                        // Tool rows persist LIVE (the tree read model grows as the turn
                        // runs), and citations keep their provenance: which call made them.
                        // The driver owns the row id so it can bind citations to it.
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

                        // Canvas artifacts the call created/updated (make/edit tools): the
                        // tool already persisted them; emit one ArtifactEvent each so the
                        // live canvas opens/updates. An existing id updates the tab in place.
                        if (outcome.Artifacts is { Count: > 0 } artifacts)
                        {
                            foreach (var artifact in artifacts)
                            {
                                await EmitAsync(new ArtifactEvent
                                {
                                    Id = artifact.Id,
                                    Kind = artifact.Kind,
                                    Name = artifact.Name,
                                    Content = artifact.Content,
                                }).ConfigureAwait(false);
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
                    await onProgress(content.ToString(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // A fault mid-stream unwinds through here with text still buffered.
            // `content`/`reasoning` reach the row via the driver's error finalize
            // regardless, but a REPLAYING client reads turn_events - flush the
            // tails (best-effort, reasoning first) so the durable log carries
            // everything that streamed. Skipped on a cancelled token: the emit
            // would throw, and the driver's cancel finalize emits its own
            // terminal event on a fresh token.
            if ((pending.Length > 0 || pendingReasoning.Length > 0) && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await FlushPendingAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Unwinding an exception already - the row's content is the backstop.
                }
            }
        }

        return new AgentLoopResult
        {
            Content = content.ToString(),
            Reasoning = reasoning.ToString(),
            TokenCount = tokenCount,
            PromptTokens = promptTokens,
            GenElapsedTicks = genElapsedTicks,
            ToolRounds = round,
        };
    }

    /// <summary>
    /// Consume one unit of the per-turn web-search budget
    /// (<see cref="AgentLoopRequest.MaxSearchCallsPerTurn"/>). Non-search calls and a
    /// disabled cap (&lt;= 0) always pass; a search past the cap returns false and
    /// the caller refuses it with a synthetic result.
    /// </summary>
    private bool TryConsumeSearchBudget(AgentLoopRequest request, string toolName, ref int used)
    {
        if (request.MaxSearchCallsPerTurn <= 0 || ResolveTool(request, toolName)?.Id != "search")
        {
            return true;
        }

        if (used >= request.MaxSearchCallsPerTurn)
        {
            _logger.LogWarning(
                "Per-turn web search budget ({MaxSearchCallsPerTurn}) exhausted - refusing this search call.",
                request.MaxSearchCallsPerTurn);
            return false;
        }

        used++;
        return true;
    }

    /// <summary>
    /// Execute one tool call. The entitlement re-check runs against the request's
    /// plan-time snapshot - the claim is the ceiling at execution time too, even
    /// off-thread (auth.md).
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        AgentLoopRequest request,
        ChatModelToolCall call,
        CancellationToken cancellationToken)
    {
        var tool = ResolveTool(request, call.Name);
        if (tool is null)
        {
            return ToolOutcome.Failure("unknown", $"no tool named '{call.Name}'");
        }

        // Defence-in-depth: never run a tool the user isn't entitled to, even if
        // it somehow reached the model (e.g. a poisoned history).
        if (!request.AllowedToolIds.Contains(tool.Id))
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
            // The mid-execution emit seam (ask_user's question_asked): the
            // driver's own persist-then-publish protocol, so a tool-emitted
            // event is durable before it is live and replays like any other.
            // No per-tool branch here - any tool may emit; null on an autonomous
            // driver (the tool fails closed rather than waiting invisibly).
            EmitAsync = request.Emit,
            Deadline = request.Host.Limits.Deadline,
            ClientTimezone = request.ClientTimezone,
            // The sub-agent's provider + nested-entitlement ceiling: delegation
            // talks to the turn's own model and can never out-tool the caller.
            ModelId = request.ModelId,
            AllowedToolIds = request.AllowedToolIds,
        };

        // The generic per-call backstop: tools carry their own tighter limits
        // (sandbox wall clock, search timeouts); this catches a hang outside
        // them. A trip fails THIS call with a visible card error - the turn
        // token cancelling rethrows as before and ends the turn. Modal tools
        // (ask_user, sub_agent) are exempt - blocking/long-running IS their job;
        // their own Deadline math is the backstop and the turn lifetime token
        // remains the hard wall.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.ToolCallTimeout > TimeSpan.Zero && tool.Type != ToolType.Modal)
        {
            callCts.CancelAfter(request.ToolCallTimeout);
        }

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(invocation, request.Host, callCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && callCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Tool '{ToolId}' timed out after {TimeoutSeconds}s (call {CallId}).",
                tool.Id, request.ToolCallTimeout.TotalSeconds, call.Id);
            return ToolOutcome.Failure(
                tool.Id,
                $"tool timed out after {request.ToolCallTimeout.TotalSeconds:0}s",
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

    /// <summary>
    /// The per-round completion bound: <see cref="AgentLoopRequest.MaxTokensPerRound"/>
    /// when set (&gt; 0), else null - the provider's own default applies.
    /// </summary>
    private static int? MaxTokensThisRound(AgentLoopRequest request) =>
        request.MaxTokensPerRound is { } cap && cap > 0 ? cap : null;

    private static ITool? ResolveTool(AgentLoopRequest request, string functionName) =>
        request.Tools.FirstOrDefault(t => string.Equals(t.Name, functionName, StringComparison.Ordinal));

    /// <summary>
    /// Whether a model-named tool call is one the turn is entitled to run - the
    /// plan-time <c>gert_tools</c> snapshot is the ceiling. An unentitled call
    /// still gets its synthetic refusal fed back to the model (the wire format
    /// needs a result per call), but it must surface NO user-facing card and
    /// persist NO tool row: a tool the user was never granted (the model
    /// hallucinated it, or a poisoned history smuggled it in) is invisible, live
    /// and on reload alike (auth.md - the claim is the ceiling).
    /// </summary>
    private static bool IsEntitledCall(AgentLoopRequest request, string functionName) =>
        ResolveTool(request, functionName) is { } tool && request.AllowedToolIds.Contains(tool.Id);

    private static string ResolveKind(AgentLoopRequest request, string functionName) =>
        ResolveTool(request, functionName)?.Id ?? functionName;

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
                    // Request is display-only (the tool card) - cap long strings so a
                    // whole-file argument (make_artifact content) doesn't bloat the
                    // event payload; the tool itself gets the full ArgumentsJson.
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
