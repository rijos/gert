using System.Runtime.CompilerServices;
using Gert.Model.Agent;
using Microsoft.Extensions.AI;

namespace Gert.Agent.Loop;

/// <summary>
/// The streaming interceptor that sits BETWEEN <see cref="GertFunctionInvokingChatClient"/> and the
/// run's inner <see cref="IChatClient"/> (decisions #13; chat-and-tools.md section the tool loop). The
/// function-invoking middleware only ever sees COMPLETED tool calls, so the Gert-specific stream
/// concerns it cannot serve are re-homed here, against the raw per-round model stream it passes through
/// untouched:
/// <list type="bullet">
///   <item>text/reasoning deltas folded into the run's <see cref="TurnAccumulators"/> and emitted as
///   <see cref="TextDelta"/>/<see cref="ReasoningDelta"/> (the consumer coalesces);</item>
///   <item>the LIVE-INTENT <see cref="ToolStarted"/> - the running card the moment a tool NAME first
///   appears mid-argument-stream (a streamed <see cref="FunctionCallContent"/> with null
///   <see cref="FunctionCallContent.Arguments"/>), gated by the entitlement snapshot so an unentitled
///   call never announces (the args-carrying second <see cref="ToolStarted"/> with the same call id
///   arrives at invocation time from the override);</item>
///   <item>the round's token counts (last-wins) and the pure generation span (the time spent consuming
///   this round's inner stream - tool execution runs in the override between inner calls, outside it).</item>
/// </list>
/// One instance per run; the middleware calls <see cref="GetStreamingResponseAsync"/> once per round.
/// </summary>
internal sealed class LiveIntentChatClient : DelegatingChatClient
{
    private readonly Toolset _toolset;
    private readonly IAgentEventSink _sink;
    private readonly TimeProvider _clock;
    private readonly TurnAccumulators _acc;

    public LiveIntentChatClient(
        IChatClient inner,
        Toolset toolset,
        IAgentEventSink sink,
        TimeProvider clock,
        TurnAccumulators accumulators)
        : base(inner)
    {
        _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _acc = accumulators ?? throw new ArgumentNullException(nameof(accumulators));
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Live intents are deduped per round: a model that dribbles the tool name across chunks must
        // announce its running card once, not once per chunk.
        var announced = new HashSet<string>(StringComparer.Ordinal);

        // The pure generation span: only the time spent consuming THIS round's stream. Tool execution
        // happens in the override between the middleware's inner calls, so it never lands inside here.
        var roundStart = _clock.GetTimestamp();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            // The contents forwarded to the middleware - rebuilt only when a live-intent signal is
            // dropped (the common case forwards the update untouched).
            List<AIContent>? kept = null;

            for (var i = 0; i < update.Contents.Count; i++)
            {
                var content = update.Contents[i];
                switch (content)
                {
                    case TextReasoningContent { Text: { Length: > 0 } reasoning }:
                        {
                            var ev = new ReasoningDelta(reasoning);
                            _acc.Deltas.Apply(ev);
                            await _sink.EmitAsync(ev, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                    case TextContent { Text: { Length: > 0 } text }:
                        {
                            var ev = new TextDelta(text);
                            _acc.Deltas.Apply(ev);
                            await _sink.EmitAsync(ev, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                    // Null arguments = live intent: a tool has been named but its arguments are still
                    // streaming. Announce the running card NOW (the user sees "Creating a file" instead
                    // of the pulse), gated by the entitlement snapshot so an unentitled call never
                    // announces. The signal is DROPPED from the forwarded stream: it is Gert's alone -
                    // the function-invoking middleware does not coalesce a name-first/args-later pair and
                    // would otherwise invoke the call twice. The completed call (non-null args) follows
                    // and is forwarded; its args-carrying ToolStarted arrives from the invocation override
                    // under the same id, updating the card in place.
                    case FunctionCallContent { Arguments: null } intent:
                        if (announced.Add(intent.CallId)
                            && _toolset.Resolve(intent.Name) is { Entitled: true } entry)
                        {
                            await _sink.EmitAsync(new ToolStarted(intent.CallId, entry.Kind, null), cancellationToken)
                                .ConfigureAwait(false);
                        }

                        // Snapshot the contents kept so far (this is the first dropped item) and skip it.
                        kept ??= [.. update.Contents.Take(i)];
                        continue;

                    case UsageContent usage:
                        if (usage.Details.OutputTokenCount is { } completion)
                        {
                            // Summed across rounds - each tool-call round generates its own output (the
                            // call's arguments + any narration); last-wins would drop every round but the
                            // final answer, so a tool turn's throughput undercounts the function-call rounds.
                            _acc.TokenCount = (_acc.TokenCount ?? 0) + (int)completion;
                        }

                        // Last non-null round wins - the largest prompt is the turn's real context footprint.
                        if (usage.Details.InputTokenCount is { } prompt)
                        {
                            _acc.PromptTokens = (int)prompt;
                        }

                        break;
                }

                kept?.Add(content);
            }

            if (kept is not null)
            {
                update.Contents = kept;
            }

            yield return update;
        }

        _acc.GenTicks += _clock.GetElapsedTime(roundStart).Ticks;
    }
}
