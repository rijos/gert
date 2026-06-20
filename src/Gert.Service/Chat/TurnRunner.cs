using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Chat;
using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Chat.Bus;
using Gert.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="ITurnRunner"/> -- the tool loop (chat-and-tools.md section the tool
/// loop), detached from any transport. Because nothing here <c>yield</c>s, the
/// model stream is consumed inside ordinary <c>try/catch</c>. Text deltas coalesce into one event per
/// <see cref="TurnOptions.DeltaFlushInterval"/> window (size-capped by
/// <see cref="TurnOptions.DeltaFlushMaxChars"/>) - the window opens at stream
/// start, so after a typical prefill the first token flushes immediately and
/// time-to-first-token stays the model's, not the turn's.
///
/// <para>
/// The emit protocol is <b>persist, then publish</b>: allocate a seq, append the
/// event to <c>turn_events</c>, then publish to the bus. The streamer's
/// subscribe-replay-dedup splice depends on exactly this order - an event is
/// always in the durable log no later than it is on the bus.
/// </para>
///
/// <para>
/// Failure semantics: a fault (model error, tool defect, the
/// <see cref="TurnOptions.MaxTurnDuration"/> timeout) finalises the assistant row
/// as <c>error</c> with whatever content streamed, and emits a terminal
/// <c>error</c> event carrying a generic message - the exception detail goes to
/// the log only, never the user-visible event (style guide section 7). The row
/// persisting is what lets resuming clients see the failure.
/// </para>
///
/// <para>
/// The shared-anchor invariant: the wall-clock cap is the budget REMAINING
/// from <see cref="TurnJob.PlannedAt"/> - the same instant
/// <see cref="MessageStatusRules"/> ages the streaming row from - so the
/// runner can only end earlier than the reader-facing orphan/409 horizon,
/// never outlive it (chat-and-tools.md section detached turns).
/// </para>
/// </summary>
public sealed class TurnRunner : ITurnRunner
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IChatClientFactory _clients;
    private readonly IConversationBus _bus;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly TurnOptions _options;
    private readonly TimeProvider _clock;
    private readonly ITurnCancellation _cancellation;
    private readonly ILogger<TurnRunner> _logger;

    public TurnRunner(
        IChatDatabaseProvider databases,
        IChatClientFactory clients,
        IConversationBus bus,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options,
        TimeProvider clock,
        ITurnCancellation cancellation,
        ILogger<TurnRunner> logger)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RunAsync(TurnJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // The turn's clock: the host token (shutdown) + the wall-clock cap, then
        // the user-cancel source the registry links in (rest-api.md section stop).
        // The cap is the REMAINING budget measured from the plan-time anchor
        // (TurnJob.PlannedAt = the placeholder's CreatedAt), not a fresh window
        // from run start: readers and the planner's 409 gate age the row from
        // that same instant (MessageStatusRules), so queue wait counts against
        // the turn and the runner always self-cancels at or before the moment
        // readers would start reporting the row as error - never after, which
        // would let a healthy running turn read as error and reopen the 409
        // gate against incomplete history.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = _options.MaxTurnDuration - (_clock.GetUtcNow() - job.PlannedAt);
        if (remaining < TimeSpan.Zero)
        {
            // Already past the horizon (a long queue wait): cancel at once.
            remaining = TimeSpan.Zero;
        }
        else if (remaining > _options.MaxTurnDuration)
        {
            // A PlannedAt in the future (clock skew) must never EXTEND the budget.
            remaining = _options.MaxTurnDuration;
        }

        lifetime.CancelAfter(remaining);

        // The same wall expressed as an instant: interactive tools (ask_user)
        // budget their wait against it so their graceful timeout result lands
        // before this lifetime token fires.
        var deadline = _clock.GetUtcNow() + remaining;

        using var registration = _cancellation.Register(TurnKey.From(job), lifetime.Token);
        var token = registration.Token;

        var topic = new ConversationTopic(job.Iss, job.Sub, job.Pid, job.ConversationId);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();

        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, token)
                .ConfigureAwait(false);

            await ExecuteTurnAsync(job, repo, topic, content, reasoning, deadline, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: best-effort error finalise (fresh repo, no token -
            // the orphan rule covers us if even this is cut short), then let the
            // worker observe the shutdown.
            await FinalizeErrorAsync(job, topic, content, reasoning, "turn interrupted by shutdown").ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (registration.IsUserCancelled)
        {
            // User stop: a normal outcome, not a fault - finalise cancelled with
            // the partial content and do NOT rethrow.
            await FinalizeCancelledAsync(job, topic, content, reasoning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Catch-all converted to a user-visible error (style guide section 7): the
            // detail goes to the log - exception only, never message content
            // (operations.md section Logging format) - and the persisted/published
            // event carries a generic message, never raw ex.Message (upstream
            // exception text can echo internal URLs or prompt fragments).
            string reason;
            if (lifetime.IsCancellationRequested)
            {
                reason = $"turn exceeded the {_options.MaxTurnDuration} limit";
                _logger.LogWarning(
                    "Turn exceeded its {MaxTurnDuration} budget for conversation {ConversationId} in project {Pid} and was cancelled.",
                    _options.MaxTurnDuration, job.ConversationId, job.Pid);
            }
            else
            {
                reason = "Something went wrong running this turn.";
                _logger.LogError(
                    ex,
                    "Turn faulted unexpectedly for conversation {ConversationId} in project {Pid}.",
                    job.ConversationId, job.Pid);
            }

            await FinalizeErrorAsync(job, topic, content, reasoning, reason).ConfigureAwait(false);
        }
    }

    private async Task ExecuteTurnAsync(
        TurnJob job,
        IChatRepository repo,
        ConversationTopic topic,
        StringBuilder content,
        StringBuilder reasoning,
        DateTimeOffset deadline,
        CancellationToken token)
    {
        await EmitAsync(repo, topic, new MessageStartEvent { MessageId = job.AssistantMessageId }, token)
            .ConfigureAwait(false);

        // The growing conversation sent upstream: optional system prompt (step 0),
        // then history, then the tool-call/tool-result pairs the loop appends.
        var messages = new List<ChatModelMessage>(job.History.Count + 4);
        if (!string.IsNullOrWhiteSpace(job.SystemPrompt))
        {
            messages.Add(new ChatModelMessage { Role = "system", Content = job.SystemPrompt });
        }

        messages.AddRange(job.History);

        var toolSpecs = job.Tools;
        int? tokenCount = null;
        int? promptTokens = null;
        long genElapsedTicks = 0;
        var collectedCitations = new List<Citation>();
        var round = 0;
        var searchCalls = 0; // consumed web_search budget (TurnOptions.MaxSearchCallsPerTurn)

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

        async Task FlushPendingReasoningAsync()
        {
            if (pendingReasoning.Length == 0)
            {
                return;
            }

            var text = pendingReasoning.ToString();
            pendingReasoning.Clear();
            lastReasoningFlushTs = _clock.GetTimestamp();
            await EmitAsync(repo, topic, new ReasoningEvent { Text = text }, token).ConfigureAwait(false);
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
            await EmitAsync(repo, topic, new DeltaEvent { Text = text }, token).ConfigureAwait(false);
        }

        // Resolve the client for this conversation's provider once - its connection
        // + sampling come from Gert:Chat:Providers; the provider is fixed for the turn.
        var model = _clients.ForProvider(job.ModelId);

        try
        {
            while (true)
            {
                var completion = new ChatCompletionRequest
                {
                    ModelId = job.ModelId,
                    Messages = messages,
                    Tools = toolSpecs,
                    // The per-round completion cap is the only sampling field the
                    // request carries; the rest rides the provider (Gert:Chat:Providers).
                    MaxTokens = MaxTokensThisRound(),
                };

                var toolCalls = new List<ChatModelToolCall>();
                var roundContentStart = content.Length;

                // Pure generation time: spans cover stream consumption only -
                // tool execution happens between rounds, outside this span.
                var roundStart = _clock.GetTimestamp();
                await foreach (var chunk in model.StreamAsync(completion, token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                    {
                        reasoning.Append(chunk.ReasoningDelta);
                        pendingReasoning.Append(chunk.ReasoningDelta);

                        var dueReasoning = _options.DeltaFlushInterval <= TimeSpan.Zero
                            || pendingReasoning.Length >= _options.DeltaFlushMaxChars
                            || _clock.GetElapsedTime(lastReasoningFlushTs) >= _options.DeltaFlushInterval;
                        if (dueReasoning)
                        {
                            await FlushPendingReasoningAsync().ConfigureAwait(false);
                        }
                    }

                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                    {
                        content.Append(chunk.TextDelta);
                        pending.Append(chunk.TextDelta);

                        var due = _options.DeltaFlushInterval <= TimeSpan.Zero
                            || pending.Length >= _options.DeltaFlushMaxChars
                            || _clock.GetElapsedTime(lastFlushTs) >= _options.DeltaFlushInterval;
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
                    if (chunk.ToolCallStart is { } toolStart && IsEntitledCall(job, toolStart.Name))
                    {
                        await FlushPendingReasoningAsync().ConfigureAwait(false);
                        await FlushPendingAsync().ConfigureAwait(false);
                        await EmitAsync(repo, topic, new ToolCallEvent
                        {
                            Id = toolStart.Id,
                            Kind = ResolveKind(toolStart.Name),
                            Status = ToolCallStatus.Running,
                            Request = null,
                        }, token).ConfigureAwait(false);
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
                var budgetExhausted = round >= _options.MaxToolRounds;
                if (budgetExhausted)
                {
                    if (toolSpecs.Count == 0)
                    {
                        _logger.LogWarning(
                            "Wind-down round still produced {CallCount} tool call(s) for conversation {ConversationId} - stopping upstream calls and finalising with the streamed content.",
                            toolCalls.Count, job.ConversationId);
                        break;
                    }

                    _logger.LogWarning(
                        "Tool budget exhausted after {MaxToolRounds} rounds for conversation {ConversationId} - refusing {CallCount} call(s) and winding the turn down.",
                        _options.MaxToolRounds, job.ConversationId, toolCalls.Count);
                    toolSpecs = [];
                }
                else
                {
                    round++;
                    _logger.LogDebug(
                        "Tool round {Round}/{MaxToolRounds} for conversation {ConversationId}: {CallCount} call(s) ({ToolNames}).",
                        round, _options.MaxToolRounds, job.ConversationId, toolCalls.Count,
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
                    // OCE lands in RunAsync's cancel finalize like any other.
                    token.ThrowIfCancellationRequested();

                    // The plan-time ceiling decides visibility: an unentitled call
                    // (a tool the model was never offered but emitted anyway) still
                    // gets a synthetic refusal in the upstream history, but shows no
                    // card and writes no tool row - invisible live and on reload.
                    var entitled = IsEntitledCall(job, call.Name);

                    if (entitled)
                    {
                        await EmitAsync(repo, topic, new ToolCallEvent
                        {
                            Id = call.Id,
                            Kind = ResolveKind(call.Name),
                            Status = ToolCallStatus.Running,
                            Request = ParseArgs(call.ArgumentsJson),
                        }, token).ConfigureAwait(false);
                    }

                    // The per-turn search budget refuses like the round budget:
                    // a synthetic failure the model reads, never a torn turn.
                    var outcome = budgetExhausted
                        ? ToolOutcome.Failure(
                            ResolveKind(call.Name),
                            $"tool budget exhausted ({_options.MaxToolRounds} rounds) - no further tool calls will run this turn; answer with what you already have")
                        : !TryConsumeSearchBudget(call.Name, ref searchCalls)
                            ? ToolOutcome.Failure(
                                ResolveKind(call.Name),
                                $"web search budget exhausted ({_options.MaxSearchCallsPerTurn} per turn) - no further searches will run this turn; answer with what you already found")
                            : await ExecuteToolAsync(job, call, repo, topic, deadline, token).ConfigureAwait(false);

                    // The result card, the durable tool row, its citations, and any
                    // canvas artifacts are all the entitled call's visible/persistent
                    // footprint - skipped wholesale for an unentitled call (a refusal
                    // never produces hits/artifacts anyway, and must leave no trace).
                    if (entitled)
                    {
                        await EmitAsync(repo, topic, new ToolResultEvent
                        {
                            Id = call.Id,
                            Kind = outcome.Kind,
                            Status = outcome.Status,
                            LatencyMs = outcome.LatencyMs,
                            Hits = outcome.Hits,
                            Stdout = outcome.Stdout,
                            Todos = outcome.Todos,
                            Error = outcome.Error,
                        }, token).ConfigureAwait(false);

                        // Tool rows persist LIVE (the tree read model grows as the turn
                        // runs), and citations keep their provenance: which call made them.
                        var toolCallRow = new ToolCall
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            MessageId = job.AssistantMessageId,
                            Kind = outcome.Kind,
                            Status = outcome.Status,
                            RequestJson = call.ArgumentsJson,
                            ResponseJson = outcome.ResponseJson,
                            LatencyMs = outcome.LatencyMs,
                            CreatedAt = _clock.GetUtcNow(),
                        };
                        await repo.InsertToolCallAsync(toolCallRow, token).ConfigureAwait(false);

                        collectedCitations.AddRange(outcome.Citations.Select(c => c with { ToolCallId = toolCallRow.Id }));

                        // Canvas artifacts the call created/updated (make/edit tools): the
                        // tool already persisted them; emit one ArtifactEvent each so the
                        // live canvas opens/updates. An existing id updates the tab in place.
                        if (outcome.Artifacts is { Count: > 0 } artifacts)
                        {
                            foreach (var artifact in artifacts)
                            {
                                await EmitAsync(repo, topic, new ArtifactEvent
                                {
                                    Id = artifact.Id,
                                    Kind = artifact.Kind,
                                    Name = artifact.Name,
                                    Content = artifact.Content,
                                }, token).ConfigureAwait(false);
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
                await repo.UpdateMessageStreamAsync(
                    job.AssistantMessageId, content.ToString(), MessageStatus.Streaming, null, token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // A fault mid-stream unwinds through here with text still buffered.
            // `content`/`reasoning` reach the row via the error finalize
            // regardless, but a REPLAYING client reads turn_events - flush the
            // tails (best-effort, reasoning first) so the durable log carries
            // everything that streamed. Skipped on a cancelled token: EmitAsync
            // would throw, and the cancel finalize emits its own terminal event
            // on a fresh token.
            if ((pending.Length > 0 || pendingReasoning.Length > 0) && !token.IsCancellationRequested)
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

        // Canvas artifacts are produced by the make_artifact / edit_artifact tools
        // during the tool loop (emitted above), not extracted from the final text -
        // a tool call's JSON content can't be truncated by the file's own ``` fences.

        // Re-number citations into one stable sequence over the whole turn, bind
        // them to the assistant message, persist, and emit after the text.
        var citations = RenumberCitations(collectedCitations, job.AssistantMessageId);
        if (citations.Count > 0)
        {
            await repo.InsertCitationsAsync(citations, token).ConfigureAwait(false);
        }

        foreach (var citation in citations)
        {
            await EmitAsync(repo, topic, new CitationEvent
            {
                Ordinal = citation.Ordinal,
                Label = citation.Label,
                DocId = citation.DocId,
                Locator = citation.Locator,
            }, token).ConfigureAwait(false);
        }

        // Finalise the row BEFORE the terminal event: a client that reacts to
        // message_end by re-reading the thread must see status=complete.
        var durationMs = (long)TimeSpan.FromTicks(genElapsedTicks).TotalMilliseconds;
        var contextTokens = promptTokens is null && tokenCount is null
            ? (int?)null
            : (promptTokens ?? 0) + (tokenCount ?? 0);

        await repo.FinalizeMessageAsync(
            job.AssistantMessageId, content.ToString(), MessageStatus.Complete, tokenCount,
            reasoning.Length > 0 ? reasoning.ToString() : null, durationMs, contextTokens, token)
            .ConfigureAwait(false);

        await EmitAsync(repo, topic, new MessageEndEvent
        {
            TokenCount = tokenCount,
            DurationMs = durationMs,
            ContextTokens = contextTokens,
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The emit protocol: allocate seq -> append to the durable log -> publish.
    /// Persist-before-publish is what makes the streamer's splice gap-free.
    /// </summary>
    private async Task EmitAsync(
        IChatRepository repo,
        ConversationTopic topic,
        ChatEvent chatEvent,
        CancellationToken token)
    {
        var seq = await repo.AllocateSeqAsync(topic.ConversationId, token).ConfigureAwait(false);

        await repo.AppendTurnEventAsync(
            new TurnEventRecord
        {
            ConversationId = topic.ConversationId,
            Seq = seq,
            Type = chatEvent.Type.ToWireName(),
            PayloadJson = JsonSerializer.Serialize(chatEvent, GertJsonOptions.Default),
            CreatedAt = _clock.GetUtcNow(),
        }, token).ConfigureAwait(false);

        _bus.Publish(topic, new TurnEvent { Seq = seq, Event = chatEvent });
    }

    /// <summary>
    /// Best-effort error finalise on a FRESH repo with no cancellation (the
    /// turn's token may be the reason we are here). Swallows its own failures -
    /// the orphan rule is the backstop.
    /// </summary>
    private async Task FinalizeErrorAsync(
        TurnJob job,
        ConversationTopic topic,
        StringBuilder content,
        StringBuilder reasoning,
        string reason)
    {
        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, CancellationToken.None)
                .ConfigureAwait(false);

            await repo.FinalizeMessageAsync(
                job.AssistantMessageId, content.ToString(), MessageStatus.Error, null,
                reasoning.Length > 0 ? reasoning.ToString() : null, null, null, CancellationToken.None)
                .ConfigureAwait(false);

            await EmitAsync(repo, topic, new ErrorEvent { Message = reason }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallowed by design - the orphan rule ages the streaming row into
            // an error - but an unlogged swallow is a defect (style guide section 7).
            _logger.LogWarning(
                ex,
                "Error finalise failed for conversation {ConversationId} in project {Pid}; the orphan rule is the backstop.",
                job.ConversationId, job.Pid);
        }
    }

    /// <summary>
    /// Best-effort cancelled finalise, mirroring <see cref="FinalizeErrorAsync"/>:
    /// fresh repo, no cancellation (the turn's token IS the reason we are here),
    /// the partial content persisted as <c>cancelled</c>, then the terminal
    /// <c>cancelled</c> event.
    /// </summary>
    private async Task FinalizeCancelledAsync(
        TurnJob job,
        ConversationTopic topic,
        StringBuilder content,
        StringBuilder reasoning)
    {
        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, CancellationToken.None)
                .ConfigureAwait(false);

            await repo.FinalizeMessageAsync(
                job.AssistantMessageId, content.ToString(), MessageStatus.Cancelled, null,
                reasoning.Length > 0 ? reasoning.ToString() : null, null, null, CancellationToken.None)
                .ConfigureAwait(false);

            await EmitAsync(repo, topic, new CancelledEvent(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallowed by design - the orphan rule ages the streaming row into
            // an error - but an unlogged swallow is a defect (style guide section 7).
            _logger.LogWarning(
                ex,
                "Cancelled finalise failed for conversation {ConversationId} in project {Pid}; the orphan rule is the backstop.",
                job.ConversationId, job.Pid);
        }
    }

    /// <summary>
    /// Consume one unit of the per-turn web-search budget
    /// (<see cref="TurnOptions.MaxSearchCallsPerTurn"/>). Non-search calls and a
    /// disabled cap (<= 0) always pass; a search past the cap returns false and
    /// the caller refuses it with a synthetic result.
    /// </summary>
    private bool TryConsumeSearchBudget(string toolName, ref int used)
    {
        if (_options.MaxSearchCallsPerTurn <= 0 || ResolveTool(toolName)?.Id != "search")
        {
            return true;
        }

        if (used >= _options.MaxSearchCallsPerTurn)
        {
            _logger.LogWarning(
                "Per-turn web search budget ({MaxSearchCallsPerTurn}) exhausted - refusing this search call.",
                _options.MaxSearchCallsPerTurn);
            return false;
        }

        used++;
        return true;
    }

    /// <summary>
    /// Execute one tool call. The entitlement re-check runs against the job's
    /// plan-time snapshot - the claim is the ceiling at execution time too, even
    /// off-thread (auth.md).
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        TurnJob job,
        ChatModelToolCall call,
        IChatRepository repo,
        ConversationTopic topic,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var tool = ResolveTool(call.Name);
        if (tool is null)
        {
            return ToolOutcome.Failure("unknown", $"no tool named '{call.Name}'");
        }

        // Defence-in-depth: never run a tool the user isn't entitled to, even if
        // it somehow reached the model (e.g. a poisoned history).
        if (!job.AllowedToolIds.Contains(tool.Id))
        {
            return ToolOutcome.Failure(tool.Id, $"tool '{tool.Id}' is not permitted");
        }

        var invocation = new ToolInvocation
        {
            Pid = job.Pid,
            ArgumentsJson = call.ArgumentsJson,
            // The artifact tools key/persist canvas artifacts on the conversation.
            ConversationId = job.ConversationId,
            MessageId = job.AssistantMessageId,
            ToolCallId = call.Id,
            // The mid-execution emit seam (ask_user's question_asked): the
            // runner's own persist-then-publish protocol, so a tool-emitted
            // event is durable before it is live and replays like any other.
            // No per-tool branch here - any tool may emit.
            EmitAsync = (chatEvent, ct) => EmitAsync(repo, topic, chatEvent, ct),
            Deadline = deadline,
            ClientTimezone = job.ClientTimezone,
            // The sub-agent's provider + nested-entitlement ceiling: delegation
            // talks to the turn's own model and can never out-tool the caller.
            ModelId = job.ModelId,
            AllowedToolIds = job.AllowedToolIds,
        };

        // The generic per-call backstop: tools carry their own tighter limits
        // (sandbox wall clock, search timeouts); this catches a hang outside
        // them. A trip fails THIS call with a visible card error - the turn
        // token cancelling rethrows as before and ends the turn. Modal tools
        // (ask_user, sub_agent) are exempt - blocking/long-running IS their job;
        // their own Deadline math is the backstop and the turn lifetime token
        // remains the hard wall.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ToolCallTimeout > TimeSpan.Zero && tool.Type != ToolType.Modal)
        {
            callCts.CancelAfter(_options.ToolCallTimeout);
        }

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(invocation, callCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && callCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Tool '{ToolId}' timed out after {TimeoutSeconds}s (call {CallId}).",
                tool.Id, _options.ToolCallTimeout.TotalSeconds, call.Id);
            return ToolOutcome.Failure(
                tool.Id,
                $"tool timed out after {_options.ToolCallTimeout.TotalSeconds:0}s",
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
    /// The per-round completion bound: <see cref="TurnOptions.MaxTokensPerRound"/>
    /// when set (&gt; 0), else null - the provider's own default applies.
    /// </summary>
    private int? MaxTokensThisRound() =>
        _options.MaxTokensPerRound > 0 ? _options.MaxTokensPerRound : null;

    private ITool? ResolveTool(string functionName) =>
        _tools.FirstOrDefault(t => string.Equals(t.Name, functionName, StringComparison.Ordinal));

    /// <summary>
    /// Whether a model-named tool call is one the turn is entitled to run - the
    /// plan-time <c>gert_tools</c> snapshot is the ceiling. An unentitled call
    /// still gets its synthetic refusal fed back to the model (the wire format
    /// needs a result per call), but it must surface NO user-facing card and
    /// persist NO tool row: a tool the user was never granted (the model
    /// hallucinated it, or a poisoned history smuggled it in) is invisible, live
    /// and on reload alike (auth.md - the claim is the ceiling).
    /// </summary>
    private bool IsEntitledCall(TurnJob job, string functionName) =>
        ResolveTool(functionName) is { } tool && job.AllowedToolIds.Contains(tool.Id);

    private string ResolveKind(string functionName) =>
        ResolveTool(functionName)?.Id ?? functionName;

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

    /// <summary>
    /// Collapse per-tool citation lists into one stable [1..n] sequence bound to
    /// the assistant message - ToolCallId provenance survives the renumber.
    /// </summary>
    private static IReadOnlyList<Citation> RenumberCitations(
        IReadOnlyList<Citation> citations,
        string assistantMessageId)
    {
        var result = new List<Citation>(citations.Count);
        for (var i = 0; i < citations.Count; i++)
        {
            result.Add(citations[i] with
            {
                MessageId = assistantMessageId,
                Ordinal = i + 1,
            });
        }

        return result;
    }
}
