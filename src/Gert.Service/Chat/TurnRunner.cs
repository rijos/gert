using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Chat.Bus;
using Gert.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="ITurnRunner"/> — the tool loop (chat-and-tools.md § the tool loop),
/// detached from any transport. Replaces the old <c>ChatService.RunAsync</c>
/// iterator: because nothing here <c>yield</c>s, the model stream is consumed
/// inside ordinary <c>try/catch</c>. Text deltas coalesce into one event per
/// <see cref="TurnOptions.DeltaFlushInterval"/> window (size-capped by
/// <see cref="TurnOptions.DeltaFlushMaxChars"/>) — the window opens at stream
/// start, so after a typical prefill the first token flushes immediately and
/// time-to-first-token stays the model's, not the turn's.
///
/// <para>
/// The emit protocol is <b>persist, then publish</b>: allocate a seq, append the
/// event to <c>turn_events</c>, then publish to the bus. The streamer's
/// subscribe-replay-dedup splice depends on exactly this order — an event is
/// always in the durable log no later than it is on the bus.
/// </para>
///
/// <para>
/// Failure semantics: a fault (model error, tool defect, the
/// <see cref="TurnOptions.MaxTurnDuration"/> timeout) finalises the assistant row
/// as <c>error</c> with whatever content streamed, and emits a terminal
/// <c>error</c> event. The row persisting (unlike the old pipeline, which
/// dropped the turn) is what lets resuming clients see the failure.
/// </para>
/// </summary>
public sealed class TurnRunner : ITurnRunner
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IChatModelClient _model;
    private readonly IConversationBus _bus;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly TurnOptions _options;
    private readonly TimeProvider _clock;
    private readonly ITurnCancellation _cancellation;
    private readonly ILogger<TurnRunner> _logger;

    public TurnRunner(
        IChatDatabaseProvider databases,
        IChatModelClient model,
        IConversationBus bus,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options,
        TimeProvider clock,
        ITurnCancellation cancellation,
        ILogger<TurnRunner> logger)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _model = model ?? throw new ArgumentNullException(nameof(model));
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
        // the user-cancel source the registry links in (rest-api.md § stop).
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_options.MaxTurnDuration);

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

            await ExecuteTurnAsync(job, repo, topic, content, reasoning, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: best-effort error finalise (fresh repo, no token —
            // the orphan rule covers us if even this is cut short), then let the
            // worker observe the shutdown.
            await FinalizeErrorAsync(job, topic, content, reasoning, "turn interrupted by shutdown").ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (registration.IsUserCancelled)
        {
            // User stop: a normal outcome, not a fault — finalise cancelled with
            // the partial content and do NOT rethrow.
            await FinalizeCancelledAsync(job, topic, content, reasoning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var reason = lifetime.IsCancellationRequested
                ? $"turn exceeded the {_options.MaxTurnDuration} limit"
                : ex.Message;
            await FinalizeErrorAsync(job, topic, content, reasoning, reason).ConfigureAwait(false);
        }
    }

    private async Task ExecuteTurnAsync(
        TurnJob job,
        IChatRepository repo,
        ConversationTopic topic,
        StringBuilder content,
        StringBuilder reasoning,
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

        // Delta coalescing: chunks buffer in `pending` (answer text) and
        // `pendingReasoning` (thinking text) and flush as ONE event each (one
        // seq = one durable row = one publish) on the time/size thresholds and
        // at every boundary. The splice stays exact — the streamer dedups by
        // seq, not by token granularity — while turn_events write amplification
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

        try
        {
            while (true)
            {
                var completion = new ChatCompletionRequest
                {
                    ModelId = job.ModelId,
                    Messages = messages,
                    Tools = toolSpecs,
                    Temperature = job.Temperature,
                    TopP = job.TopP,
                    PresencePenalty = job.PresencePenalty,
                    MaxTokens = job.MaxTokens,
                    Stop = job.Stop,
                    Seed = job.Seed,
                    EnableThinking = job.Thinking,
                    PreserveThinking = job.PreserveThinking,
                };

                var toolCalls = new List<ChatModelToolCall>();
                var roundContentStart = content.Length;

                // Pure generation time: spans cover stream consumption only —
                // tool execution happens between rounds, outside this span.
                var roundStart = _clock.GetTimestamp();
                await foreach (var chunk in _model.StreamAsync(completion, token).ConfigureAwait(false))
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
                        // Last round wins — the largest prompt is the turn's
                        // real context footprint.
                        promptTokens = chunk.PromptTokenCount;
                    }
                }

                genElapsedTicks += _clock.GetElapsedTime(roundStart).Ticks;

                // Boundary flush: all of a round's text precedes its tool events,
                // and the final round's text precedes citations/message_end.
                await FlushPendingAsync().ConfigureAwait(false);

                // No tool calls → the model produced its final answer; leave the loop.
                if (toolCalls.Count == 0)
                {
                    break;
                }

                // Round cap: past the budget the round's calls are NOT executed —
                // each is refused with a synthetic error result instead (the wire
                // format requires a result per call, and dropping the round whole
                // would also drop the narration the model must see next round),
                // tools stop being advertised, and the model gets ONE wind-down
                // round to answer with what it has. (Clearing the tools array
                // re-renders the templated system/tools region upstream and so
                // invalidates the vLLM prefix cache for this final round —
                // acceptable for a runaway loop; tool_choice:"none" would preserve
                // the prefix if vLLM support is ever confirmed.) If the wind-down
                // round STILL emits tool calls — a tool-heavy history invites
                // imitation even with nothing advertised — stop calling upstream
                // and finalise with what already streamed: before this brake the
                // loop spun against vLLM until MaxTurnDuration, 409-blocking the
                // conversation the whole time.
                var budgetExhausted = round >= _options.MaxToolRounds;
                if (budgetExhausted)
                {
                    if (toolSpecs.Count == 0)
                    {
                        _logger.LogWarning(
                            "Wind-down round still produced {CallCount} tool call(s) for conversation {ConversationId} — stopping upstream calls and finalising with the streamed content.",
                            toolCalls.Count, job.ConversationId);
                        break;
                    }

                    _logger.LogWarning(
                        "Tool budget exhausted after {MaxToolRounds} rounds for conversation {ConversationId} — refusing {CallCount} call(s) and winding the turn down.",
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
                // THIS round rides along as content — a model that narrates while it
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
                    await EmitAsync(repo, topic, new ToolCallEvent
                    {
                        Id = call.Id,
                        Kind = ResolveKind(call.Name),
                        Status = ToolCallStatus.Running,
                        Request = ParseArgs(call.ArgumentsJson),
                    }, token).ConfigureAwait(false);

                    var outcome = budgetExhausted
                        ? ToolOutcome.Failure(
                            ResolveKind(call.Name),
                            $"tool budget exhausted ({_options.MaxToolRounds} rounds) — no further tool calls will run this turn; answer with what you already have")
                        : await ExecuteToolAsync(job, call, token).ConfigureAwait(false);

                    await EmitAsync(repo, topic, new ToolResultEvent
                    {
                        Id = call.Id,
                        Kind = outcome.Kind,
                        Status = outcome.Status,
                        LatencyMs = outcome.LatencyMs,
                        Hits = outcome.Hits,
                        Stdout = outcome.Stdout,
                        Todos = outcome.Todos,
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
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                    await repo.InsertToolCallAsync(toolCallRow, token).ConfigureAwait(false);

                    collectedCitations.AddRange(outcome.Citations.Select(c => c with { ToolCallId = toolCallRow.Id }));

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
            // regardless, but a REPLAYING client reads turn_events — flush the
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
                    // Unwinding an exception already — the row's content is the backstop.
                }
            }
        }

        // Artifact extraction (implementation-plan U7b): named fences in the final
        // content become canvas artifacts — persisted first (the thread read model
        // returns them on reload), then emitted so the live canvas tab opens.
        foreach (var extracted in ArtifactExtractor.Extract(content.ToString()))
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = job.ConversationId,
                MessageId = job.AssistantMessageId,
                Kind = extracted.Kind,
                Name = extracted.Name,
                Language = extracted.Language,
                Content = extracted.Content,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await repo.InsertArtifactAsync(artifact, token).ConfigureAwait(false);

            await EmitAsync(repo, topic, new ArtifactEvent
            {
                Id = artifact.Id,
                Kind = artifact.Kind,
                Name = artifact.Name,
                Content = artifact.Content,
            }, token).ConfigureAwait(false);
        }

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
    /// The emit protocol: allocate seq → append to the durable log → publish.
    /// Persist-before-publish is what makes the streamer's splice gap-free.
    /// </summary>
    private async Task EmitAsync(
        IChatRepository repo,
        ConversationTopic topic,
        ChatEvent chatEvent,
        CancellationToken token)
    {
        var seq = await repo.AllocateSeqAsync(topic.ConversationId, token).ConfigureAwait(false);

        await repo.AppendTurnEventAsync(new TurnEventRecord
        {
            ConversationId = topic.ConversationId,
            Seq = seq,
            Type = chatEvent.Type.ToWireName(),
            PayloadJson = JsonSerializer.Serialize(chatEvent, GertJsonOptions.Default),
            CreatedAt = DateTimeOffset.UtcNow,
        }, token).ConfigureAwait(false);

        _bus.Publish(topic, new TurnEvent { Seq = seq, Event = chatEvent });
    }

    /// <summary>
    /// Best-effort error finalise on a FRESH repo with no cancellation (the
    /// turn's token may be the reason we are here). Swallows its own failures —
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
        catch
        {
            // Orphan rule territory: the streaming row ages into an error.
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
        catch
        {
            // Orphan rule territory: the streaming row ages into an error.
        }
    }

    /// <summary>
    /// Execute one tool call. The entitlement re-check runs against the job's
    /// plan-time snapshot — the claim is the ceiling at execution time too, even
    /// off-thread (auth.md).
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        TurnJob job,
        ChatModelToolCall call,
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

        var invocation = new ToolInvocation { Pid = job.Pid, ArgumentsJson = call.ArgumentsJson };
        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
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

    private ITool? ResolveTool(string functionName) =>
        _tools.FirstOrDefault(t => string.Equals(t.Name, functionName, StringComparison.Ordinal));

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
                    JsonValueKind.String => prop.Value.GetString(),
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
    }

    /// <summary>
    /// Collapse per-tool citation lists into one stable [1..n] sequence bound to
    /// the assistant message — ToolCallId provenance survives the renumber.
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
