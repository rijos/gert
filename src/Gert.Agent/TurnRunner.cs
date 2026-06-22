using System.Text.Json;
using Gert.Agent.Hosting;
using Gert.Agent.Loop;
using Gert.Chat;
using Gert.Database;
using Gert.Model;
using Gert.Model.Agent;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Rag;
using Gert.Service.Chat;
using Gert.Service.Chat.Bus;
using Gert.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Agent;

/// <summary>
/// <see cref="ITurnRunner"/> -- the chat-shell driver for the tool loop
/// (chat-and-tools.md section the tool loop): it opens the conversation's
/// <c>chat.db</c>, builds the per-turn <c>ChatToolHost</c>, drives the reusable
/// <see cref="IAgentLoop"/>, and owns everything the loop does NOT - message_start,
/// the tool_call-row insert + citation collection (via the loop's callbacks),
/// citation renumber/persist/emit, FinalizeMessage + message_end, and the
/// error/cancel finalizers. The loop streams the model and runs tools; the shell
/// is its persistence + transport.
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
    /// <summary>Read-only built-ins a sub-agent may use (never itself - delegation cannot recurse).</summary>
    private static readonly string[] DelegableToolIds = ["rag", "search", "fetch", "clock"];

    private readonly IChatDatabaseProvider _databases;
    private readonly IChatClientFactory _clients;
    private readonly IConversationBus _bus;
    private readonly IAgent _agent;
    private readonly IAgentLoop _loop;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IRagIndexProvider _ragProvider;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly TurnOptions _options;
    private readonly ToolsOptions _toolsOptions;
    private readonly TimeProvider _clock;
    private readonly ITurnCancellation _cancellation;
    private readonly ITurnQuestions _questions;
    private readonly ILogger<TurnRunner> _logger;

    public TurnRunner(
        IChatDatabaseProvider databases,
        IChatClientFactory clients,
        IConversationBus bus,
        IAgent agent,
        IAgentLoop loop,
        IEnumerable<ITool> tools,
        IRagIndexProvider ragProvider,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IOptions<TurnOptions> options,
        IOptions<ToolsOptions> toolsOptions,
        TimeProvider clock,
        ITurnCancellation cancellation,
        ITurnQuestions questions,
        ILogger<TurnRunner> logger)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _ragProvider = ragProvider ?? throw new ArgumentNullException(nameof(ragProvider));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _toolsOptions = toolsOptions?.Value ?? throw new ArgumentNullException(nameof(toolsOptions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
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

        // The partial-content fold: the tee applies each AgentEvent here too, so a fault mid-stream
        // leaves the error/cancel finalize the content/reasoning that streamed (the loop's own
        // accumulator is unreachable once it throws). Same fold as DeltaAccumulator everywhere.
        var acc = new DeltaAccumulator();

        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, token)
                .ConfigureAwait(false);

            await ExecuteTurnAsync(job, repo, topic, acc, deadline, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: best-effort error finalise (fresh repo, no token -
            // the orphan rule covers us if even this is cut short), then let the
            // worker observe the shutdown.
            await FinalizeErrorAsync(job, topic, acc, "turn interrupted by shutdown").ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (registration.IsUserCancelled)
        {
            // User stop: a normal outcome, not a fault - finalise cancelled with
            // the partial content and do NOT rethrow.
            await FinalizeCancelledAsync(job, topic, acc).ConfigureAwait(false);
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

            await FinalizeErrorAsync(job, topic, acc, reason).ConfigureAwait(false);
        }
    }

    private async Task ExecuteTurnAsync(
        TurnJob job,
        IChatRepository repo,
        ConversationTopic topic,
        DeltaAccumulator acc,
        DateTimeOffset deadline,
        CancellationToken token)
    {
        await EmitAsync(repo, topic, new MessageStartEvent { MessageId = job.AssistantMessageId }, token)
            .ConfigureAwait(false);

        // The initial conversation sent upstream: optional system prompt (step 0),
        // then history. The loop copies this and appends the tool-call/tool-result
        // pairs onto its own working list.
        var messages = new List<ChatMessage>(job.History.Count + 1);
        if (!string.IsNullOrWhiteSpace(job.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, job.SystemPrompt));
        }

        messages.AddRange(job.History);

        // Citations collected across the turn, each bound to the tool_call row that
        // produced it - renumbered into one stable sequence after the loop returns.
        var collectedCitations = new List<Citation>();

        // The chat IToolHost, built ONCE for the turn: pre-scoped to this
        // conversation's object store (the canvas artifact tools) + the project's
        // RAG index (search_documents), carrying the turn deadline, a ChatToolUi for
        // the human-interaction port (ask_user) wired to the question registry + the
        // runner's own persist-then-publish emit, and a ChatToolDelegate over the same
        // IAgentLoop the turn runs (run_sub_agent).
        Task Emit(ChatEvent ev, CancellationToken ct) => EmitAsync(repo, topic, ev, ct);
        var objects = new ChatObjectResource(repo, job.ConversationId, _clock);
        var rag = new ProjectRagResource(_ragProvider, _embeddings, job.Iss, job.Sub, job.Pid);
        var ui = new ChatToolUi(
            _questions,
            Emit,
            new TurnKey(job.Iss, job.Sub, job.Pid, job.ConversationId),
            _clock,
            _options.AskUserTimeout,
            deadline);

        // The sub-agent's delegable tools: the read-only built-ins intersected with
        // the parent turn's entitlement snapshot, so a nested tool can never out-tool
        // the parent (auth.md). The nested host is AUTONOMOUS - no Ui (no ask_user),
        // a no-op delegate (no recursion), throwing Objects (the delegable set never
        // touches objects), the same project RAG. The intersected id set is the
        // ceiling the nested loop re-checks each call against.
        var delegableTools = _tools
            .Where(t => DelegableToolIds.Contains(t.Id, StringComparer.Ordinal)
                        && job.AllowedToolIds.Contains(t.Id))
            .ToList();
        var delegableIds = delegableTools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var nestedHost = new ChatToolHost(
            new NotSupportedObjectResource(), rag, ui: null, new NoOpToolDelegate(), deadline);
        var subAgent = new ChatToolDelegate(
            _loop,
            _clients.ForProvider(job.ModelId),
            job.ModelId,
            delegableTools,
            delegableIds,
            nestedHost,
            _options.MaxTokensPerRound > 0 ? _options.MaxTokensPerRound : null,
            _toolsOptions.PerTool);

        var host = new ChatToolHost(objects, rag, ui, subAgent, deadline);

        // The per-run tool view: all tools, the advertised specs, the plan-time entitlement
        // snapshot, and the operator's per-tool bound overrides. Effective bounds are computed once
        // here (each tool's intrinsic ToolBounds with non-null overrides applied) and tracked for the
        // run - the loop never fetches config (turn-budgets.md section 1).
        var toolset = new Toolset(_tools, job.Tools, job.AllowedToolIds, _toolsOptions.PerTool);

        // The live-path coalescer: batches the loop's raw text/reasoning deltas into one durable
        // row each, emitting through the persist-then-publish seam. The accumulator (acc) folds the
        // same deltas for the progress flush + the fault finalizers; the coalescer for the wire.
        var coalescer = new DeltaCoalescer(
            Emit, _options.DeltaFlushInterval, _options.DeltaFlushMaxChars, _clock);

        // The tee: map each AgentEvent the loop emits to the persisted/published ChatEvent(s) and the
        // live tool_call row + citation collection. This is the ONE place the agent's compute
        // vocabulary becomes the conversation event log (refactor: split the noun).
        async ValueTask OnAgentEvent(AgentEvent ev, CancellationToken ct)
        {
            switch (ev)
            {
                case TextDelta t:
                    acc.Apply(t);
                    await coalescer.AppendText(t.Text, ct).ConfigureAwait(false);
                    break;
                case ReasoningDelta r:
                    acc.Apply(r);
                    await coalescer.AppendReasoning(r.Text, ct).ConfigureAwait(false);
                    break;
                case ToolStarted ts:
                    // Deltas precede the card: flush the coalescer boundary first.
                    await coalescer.FlushBoundary(ct).ConfigureAwait(false);
                    await Emit(
                        new ToolCallEvent
                        {
                            Id = ts.CallId,
                            Kind = ts.Kind,
                            Status = ToolCallStatus.Running,
                            Request = ts.Request,
                        },
                        ct).ConfigureAwait(false);
                    break;
                case ToolCompleted tc:
                    await EmitToolCompletedAsync(tc.Call, ct).ConfigureAwait(false);
                    break;
                case RoundCompleted:
                    // Tool boundary: flush buffered text, then the streaming-row progress flush so
                    // thread reads see progress.
                    await coalescer.FlushBoundary(ct).ConfigureAwait(false);
                    await repo.UpdateMessageStreamAsync(
                        job.AssistantMessageId, acc.Content, MessageStatus.Streaming, null, ct)
                        .ConfigureAwait(false);
                    break;
                case TurnFinished:
                    // The terminal finalize runs after the loop returns, off the returned result.
                    break;
            }
        }

        // Render one entitled tool result: the card, the LIVE tool_call row (the tree read model
        // grows as the turn runs), its citations bound to the new row id, and any canvas artifacts.
        async Task EmitToolCompletedAsync(ExecutedToolCall executed, CancellationToken ct)
        {
            await Emit(
                new ToolResultEvent
                {
                    Id = executed.CallId,
                    Kind = executed.Kind,
                    Status = executed.Status,
                    LatencyMs = executed.LatencyMs,
                    Hits = executed.Hits,
                    Stdout = executed.Stdout,
                    Todos = executed.Todos,
                    Error = executed.Error,
                },
                ct).ConfigureAwait(false);

            var rowId = Guid.NewGuid().ToString("D");
            await repo.InsertToolCallAsync(
                new ToolCall
                {
                    Id = rowId,
                    MessageId = job.AssistantMessageId,
                    Kind = executed.Kind,
                    Status = executed.Status,
                    RequestJson = executed.RequestJson,
                    ResponseJson = executed.ResponseJson,
                    LatencyMs = executed.LatencyMs,
                    CreatedAt = _clock.GetUtcNow(),
                },
                ct).ConfigureAwait(false);

            collectedCitations.AddRange(executed.Citations.Select(c => c with { ToolCallId = rowId }));

            if (executed.Artifacts is { Count: > 0 } artifacts)
            {
                foreach (var artifact in artifacts)
                {
                    await Emit(
                        new ArtifactEvent
                        {
                            Id = artifact.Id,
                            Kind = artifact.Kind,
                            Name = artifact.Name,
                            Content = artifact.Content,
                        },
                        ct).ConfigureAwait(false);
                }
            }
        }

        // Start the agent - compute in the background behind a channel - and tee its events into the
        // conversation event log as they arrive (while it's busy, I get stuff back). The provider's
        // connection + sampling come from Gert:Chat:Providers; the provider is fixed for the turn.
        var run = _agent.Start(
            new AgentLoopRequest
            {
                Messages = messages,
                Tools = toolset,
                ModelId = job.ModelId,
                Model = _clients.ForProvider(job.ModelId),
                Host = host,
                Pid = job.Pid,
                ConversationId = job.ConversationId,
                MessageId = job.AssistantMessageId,
                ClientTimezone = job.ClientTimezone,
                MaxRounds = _options.MaxToolRounds,
                MaxTokensPerRound = _options.MaxTokensPerRound,
            },
            token);

        AgentResult result;
        try
        {
            // Drain to channel completion (not the token): the loop's finally completes the channel on
            // success AND on fault/cancel, so the tee always sees every event the loop wrote before it
            // ended - acc then holds the true partial content for the cancel finalize.
            await foreach (var ev in run.Events.ConfigureAwait(false))
            {
                await OnAgentEvent(ev, token).ConfigureAwait(false);
            }

            // Observe the loop's outcome (rethrows a fault / cancel to the finalizers above), then
            // flush the final round's trailing text before the terminal events.
            result = await run.Completion.ConfigureAwait(false);
            await coalescer.FlushBoundary(token).ConfigureAwait(false);
        }
        finally
        {
            // Backstop on a fault mid-stream: flush whatever is still buffered so the durable log
            // carries it (a cancelled token skips - the cancel finalize writes acc.Content instead).
            await coalescer.FlushTails(token).ConfigureAwait(false);
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
        var durationMs = (long)TimeSpan.FromTicks(result.GenElapsedTicks).TotalMilliseconds;
        var contextTokens = result.PromptTokens is null && result.TokenCount is null
            ? (int?)null
            : (result.PromptTokens ?? 0) + (result.TokenCount ?? 0);

        await repo.FinalizeMessageAsync(
            job.AssistantMessageId, result.Content, MessageStatus.Complete, result.TokenCount,
            result.Reasoning.Length > 0 ? result.Reasoning : null, durationMs, contextTokens, token)
            .ConfigureAwait(false);

        await EmitAsync(repo, topic, new MessageEndEvent
        {
            TokenCount = result.TokenCount,
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
        DeltaAccumulator acc,
        string reason)
    {
        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, CancellationToken.None)
                .ConfigureAwait(false);

            await repo.FinalizeMessageAsync(
                job.AssistantMessageId, acc.Content, MessageStatus.Error, null,
                acc.Reasoning.Length > 0 ? acc.Reasoning : null, null, null, CancellationToken.None)
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
        DeltaAccumulator acc)
    {
        try
        {
            await using var repo = await _databases
                .OpenAsync(job.Iss, job.Sub, job.Pid, CancellationToken.None)
                .ConfigureAwait(false);

            await repo.FinalizeMessageAsync(
                job.AssistantMessageId, acc.Content, MessageStatus.Cancelled, null,
                acc.Reasoning.Length > 0 ? acc.Reasoning : null, null, null, CancellationToken.None)
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
