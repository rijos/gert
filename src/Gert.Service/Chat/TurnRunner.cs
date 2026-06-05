using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Chat.Bus;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Microsoft.Extensions.Options;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="ITurnRunner"/> — the tool loop (chat-and-tools.md § the tool loop),
/// detached from any transport. Replaces the old <c>ChatService.RunAsync</c>
/// iterator: because nothing here <c>yield</c>s, the model stream is consumed
/// inside ordinary <c>try/catch</c> and every chunk flows out the moment it
/// arrives — no per-call buffering (the old <c>DrainModelCallAsync</c>), so
/// time-to-first-token is the model's, not the turn's.
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
    /// <summary>Hard cap on tool rounds per turn, to bound a runaway tool loop.</summary>
    private const int MaxToolRounds = 5;

    private readonly IDatabaseProvider _databases;
    private readonly IChatModelClient _model;
    private readonly IConversationBus _bus;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly TurnOptions _options;

    public TurnRunner(
        IDatabaseProvider databases,
        IChatModelClient model,
        IConversationBus bus,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task RunAsync(TurnJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // The turn's only clock: the host token (shutdown) + the wall-clock cap.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_options.MaxTurnDuration);
        var token = lifetime.Token;

        var topic = new ConversationTopic(job.Iss, job.Sub, job.Pid, job.ConversationId);
        var content = new StringBuilder();

        try
        {
            await using var repo = await _databases
                .OpenChatAsync(job.Iss, job.Sub, job.Pid, token)
                .ConfigureAwait(false);

            await ExecuteTurnAsync(job, repo, topic, content, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: best-effort error finalise (fresh repo, no token —
            // the orphan rule covers us if even this is cut short), then let the
            // worker observe the shutdown.
            await FinalizeErrorAsync(job, topic, content, "turn interrupted by shutdown").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var reason = lifetime.IsCancellationRequested
                ? $"turn exceeded the {_options.MaxTurnDuration} limit"
                : ex.Message;
            await FinalizeErrorAsync(job, topic, content, reason).ConfigureAwait(false);
        }
    }

    private async Task ExecuteTurnAsync(
        TurnJob job,
        IChatRepository repo,
        ConversationTopic topic,
        StringBuilder content,
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
        var collectedCitations = new List<Citation>();
        var round = 0;

        while (true)
        {
            var completion = new ChatCompletionRequest
            {
                ModelId = job.ModelId,
                Messages = messages,
                Tools = toolSpecs,
                Temperature = job.Temperature,
                TopP = job.TopP,
                MaxTokens = job.MaxTokens,
                Stop = job.Stop,
                Seed = job.Seed,
            };

            // Stream the model call LIVE: each text delta is emitted the moment it
            // arrives (per-chunk durable rows keep replay seq-exact with the live
            // stream — coarser coalescing would desync the splice watermark).
            var toolCalls = new List<ChatModelToolCall>();
            await foreach (var chunk in _model.StreamAsync(completion, token).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    content.Append(chunk.TextDelta);
                    await EmitAsync(repo, topic, new DeltaEvent { Text = chunk.TextDelta }, token)
                        .ConfigureAwait(false);
                }

                if (chunk.ToolCall is not null)
                {
                    toolCalls.Add(chunk.ToolCall);
                }

                if (chunk.TokenCount is not null)
                {
                    tokenCount = chunk.TokenCount;
                }
            }

            // No tool calls → the model produced its final answer; leave the loop.
            if (toolCalls.Count == 0)
            {
                break;
            }

            // Round cap: stop offering tools and let the model answer with what it has.
            if (round >= MaxToolRounds)
            {
                toolSpecs = [];
                continue;
            }

            round++;

            foreach (var call in toolCalls)
            {
                // The assistant turn that asked for the tool must precede its
                // result in the upstream history.
                messages.Add(new ChatModelMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCallId = call.Id,
                });

                await EmitAsync(repo, topic, new ToolCallEvent
                {
                    Id = call.Id,
                    Kind = ResolveKind(call.Name),
                    Status = ToolCallStatus.Running,
                    Request = ParseArgs(call.ArgumentsJson),
                }, token).ConfigureAwait(false);

                var outcome = await ExecuteToolAsync(job, call, token).ConfigureAwait(false);

                await EmitAsync(repo, topic, new ToolResultEvent
                {
                    Id = call.Id,
                    Kind = outcome.Kind,
                    Status = outcome.Status,
                    LatencyMs = outcome.LatencyMs,
                    Hits = outcome.Hits,
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
        await repo.UpdateMessageStreamAsync(
            job.AssistantMessageId, content.ToString(), MessageStatus.Complete, tokenCount, token)
            .ConfigureAwait(false);

        await EmitAsync(repo, topic, new MessageEndEvent { TokenCount = tokenCount }, token)
            .ConfigureAwait(false);
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
        string reason)
    {
        try
        {
            await using var repo = await _databases
                .OpenChatAsync(job.Iss, job.Sub, job.Pid, CancellationToken.None)
                .ConfigureAwait(false);

            await repo.UpdateMessageStreamAsync(
                job.AssistantMessageId, content.ToString(), MessageStatus.Error, null, CancellationToken.None)
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
