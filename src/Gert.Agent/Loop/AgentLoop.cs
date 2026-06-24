using Gert.Model.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Gert.Agent.Loop;

/// <summary>
/// <see cref="IAgentLoop"/> -- the reusable tool loop (chat-and-tools.md section the tool loop) the chat
/// shell, the sub-agent, and a headless driver all run. <see cref="RunAsync"/> assembles the
/// Microsoft.Extensions.AI tool-calling pipeline for ONE turn and drives it to its final answer
/// (decisions #13):
/// <list type="number">
///   <item>a <see cref="LiveIntentChatClient"/> in front of the run's inner <see cref="IChatClient"/> -
///   it folds the streamed content/reasoning, token counts, and the pure generation span into the
///   run's <see cref="TurnAccumulators"/> and emits the text/reasoning deltas + the live-intent running
///   card;</item>
///   <item>a <see cref="GertFunctionInvokingChatClient"/> over that - the function-invoking middleware
///   whose override carries Gert's entitlement re-check, round + per-tool budgets, per-call timeout, and
///   per-call host, and emits the tool started/completed + round-boundary events.</item>
/// </list>
/// The loop's only output is <see cref="AgentEvent"/>, emitted through the one
/// <see cref="IAgentEventSink"/>: it knows nothing of <c>IChatRepository</c> / <c>IConversationBus</c> /
/// coalescing / persistence. Driving the middleware's streamed response to completion runs the whole
/// turn (both halves emit as it flows); the final <see cref="AgentResult"/> is read back off the
/// accumulators. Stateless beyond the clock - safe as a singleton (it news up a fresh per-turn pipeline
/// over the inner client each call, capturing no per-turn state on itself).
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
    public async Task<AgentResult> RunAsync(
        AgentLoopRequest request,
        IAgentEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        var toolset = request.Tools;
        var acc = new TurnAccumulators();

        // The two-layer pipeline: the interceptor folds the stream + emits deltas/live-intent; the
        // function-invoking middleware owns the round loop, the history shaping (the assistant
        // tool-calls message carries this round's narration back for free), and the wind-down. The
        // middleware never disposes here (no `using`): disposing it would dispose the shared inner
        // client the run was handed.
        var interceptor = new LiveIntentChatClient(request.Model, toolset, sink, _clock, acc);
        var pipeline = new GertFunctionInvokingChatClient(interceptor, request, sink, _clock, _logger, acc)
        {
            // MaxRounds executed rounds + 1 refused round; the middleware then adds ONE final
            // tools-cleared round (the answer-only wind-down) and stops even if that round still calls.
            MaximumIterationsPerRequest = request.MaxRounds + 1,
        };

        var options = new ChatOptions
        {
            // The advertised lean ToolFunctions (the tool's own compact schema). A non-empty list maps
            // tool_choice:"auto"; null withdraws tools entirely. The middleware clears them itself on
            // the wind-down round.
            Tools = toolset.AdvertisedTools.Count > 0 ? toolset.AdvertisedTools.ToList() : null,

            // The per-round completion bound: MaxTokensPerRound when set (> 0), else null - the
            // provider's own default applies. The only sampling field the loop carries; the rest rides
            // the provider inside the IChatClient.
            MaxOutputTokens = request.MaxTokensPerRound is { } cap && cap > 0 ? cap : null,
        };

        // The caller's initial messages (system + history) copied into a working list the middleware
        // grows with the round's tool-call/tool-result pairs (the caller's list is never mutated).
        var messages = new List<ChatMessage>(request.Messages.Count + 4);
        messages.AddRange(request.Messages);

        // Drive the middleware to completion: both pipeline halves emit through the sink as the stream
        // flows, so the loop only has to consume it. (The final answer text is already folded into the
        // accumulator by the interceptor; nothing here needs the surfaced updates.)
        await foreach (var _ in pipeline.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
        }

        var result = new AgentResult
        {
            Content = acc.Deltas.Content,
            Reasoning = acc.Deltas.Reasoning,
            TokenCount = acc.TokenCount,
            PromptTokens = acc.PromptTokens,
            GenElapsedTicks = acc.GenTicks,
            ToolRounds = acc.ToolRounds,
        };

        await sink.EmitAsync(new TurnFinished(result), cancellationToken).ConfigureAwait(false);
        return result;
    }
}
