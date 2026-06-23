using System.Diagnostics;
using System.Text.Json;
using Gert.Model.Agent;
using Gert.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Gert.Agent.Loop;

/// <summary>
/// Gert's <see cref="FunctionInvokingChatClient"/> subclass - the tool loop's orchestration
/// (chat-and-tools.md section the tool loop; decisions #13). The base middleware owns the
/// while-loop, the streamed-response-to-history shaping (the assistant tool-calls message carrying the
/// round's narration rides back for free - qwen narrates while it calls tools), and the wind-down: with
/// <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/> set to
/// <c>MaxRounds + 1</c>, the base runs the executed rounds plus ONE refused round (the override turns
/// every call into a synthetic budget-exhausted result the model reads), then ONE final tools-cleared
/// round so the model answers with what it has; if even that round emits calls the base stops.
///
/// <para>
/// What the middleware does NOT model is dispatched in the <see cref="InvokeFunctionAsync"/> override,
/// IN THIS ORDER: the plan-time entitlement re-check (the claim is the ceiling, off-thread too - an
/// unentitled call is refused INVISIBLY, no card and no row, auth.md); the round budget (past
/// <c>MaxRounds</c> every call is refused); the per-tool call ceiling
/// (<see cref="Toolset.TryConsumeCall"/>); the per-call timeout (Modal tools exempt, a trip is a visible
/// card error not a torn turn); the per-call <see cref="BudgetedToolHost"/> + card. A Gert
/// <see cref="ITool"/> is run directly through that host (its <see cref="ToolFunction"/> advertise-time
/// shape is never invoked), and its model-facing JSON is RETURNED to the base as the tool result -
/// refusals return a value too, never throw, so the base's consecutive-error brake never trips. The
/// override emits the args-carrying <see cref="ToolStarted"/>, the <see cref="ToolCompleted"/>, and the
/// per-round <see cref="RoundCompleted"/> (the streaming-row progress beat) through the run's
/// <see cref="IAgentEventSink"/>. Built fresh per turn over the run's inner client; not shared.
/// </para>
/// </summary>
internal sealed class GertFunctionInvokingChatClient : FunctionInvokingChatClient
{
    private readonly AgentLoopRequest _request;
    private readonly Toolset _toolset;
    private readonly IAgentEventSink _sink;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TurnAccumulators _acc;

    public GertFunctionInvokingChatClient(
        IChatClient inner,
        AgentLoopRequest request,
        IAgentEventSink sink,
        TimeProvider clock,
        ILogger logger,
        TurnAccumulators accumulators)
        : base(inner)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _toolset = request.Tools;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _acc = accumulators ?? throw new ArgumentNullException(nameof(accumulators));
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        // A user stop (or shutdown/timeout) mid-round unwinds here - the OCE flows out through the
        // middleware to the driver's cancel finalize, same as the hand-rolled loop's per-call check.
        cancellationToken.ThrowIfCancellationRequested();

        var call = context.CallContent;
        var entry = _toolset.Resolve(call.Name);
        var argumentsJson = SerializeArguments(call.Arguments);

        // The plan-time ceiling decides visibility: an unentitled call (a tool the model was never
        // offered but emitted anyway, or one outside the entitlement snapshot) still gets a synthetic
        // refusal in the upstream history, but shows no card and writes no tool row - invisible live and
        // on reload (auth.md).
        var entitled = entry?.Entitled ?? false;
        var kind = entry?.Kind ?? call.Name;

        string responseJson;
        if (!entitled)
        {
            var refusal = entry is null
                ? $"no tool named '{call.Name}'"
                : $"tool '{entry.Tool.Id}' is not permitted";
            responseJson = ToolOutcome.Failure(kind, refusal).ResponseJson ?? string.Empty;
        }
        else
        {
            await _sink.EmitAsync(
                new ToolStarted(call.CallId, kind, DisplayArguments(call.Arguments)),
                cancellationToken).ConfigureAwait(false);

            // Round budget: past MaxRounds (the refused round the middleware runs before its wind-down),
            // every call is refused with a synthetic result the model reads, never a torn turn.
            ToolOutcome outcome;
            if (context.Iteration >= _request.MaxRounds)
            {
                _logger.LogWarning(
                    "Tool budget exhausted after {MaxToolRounds} rounds - refusing call '{ToolName}' and winding the turn down.",
                    _request.MaxRounds, call.Name);
                outcome = ToolOutcome.Failure(
                    kind,
                    $"tool budget exhausted ({_request.MaxRounds} rounds) - no further tool calls will run this turn; answer with what you already have");
            }
            else
            {
                // Within the round budget this counts as an executed tool round (idempotent within the
                // round - every call of the round writes the same iteration+1).
                _acc.ToolRounds = context.Iteration + 1;
                outcome = !_toolset.TryConsumeCall(entry!)
                    ? ToolOutcome.Failure(
                        kind,
                        $"tool '{entry!.Tool.Id}' call budget exhausted ({entry.Effective.MaxCallsPerTurn} per turn) - no further '{entry.Tool.Name}' calls will run this turn; answer with what you already have")
                    : await ExecuteToolAsync(entry!, call, argumentsJson, cancellationToken).ConfigureAwait(false);
            }

            // The result card, the durable tool row, its citations, and any canvas artifacts are all the
            // entitled call's visible/persistent footprint, carried in ONE event the consumer unpacks.
            await _sink.EmitAsync(
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

            responseJson = outcome.ResponseJson ?? string.Empty;
        }

        // The tool boundary: the LAST call of a round is a discrete beat the consumer maps to the
        // streaming-row progress flush (emitted once per round, after the round's results, even when the
        // round's only call was refused/unentitled).
        if (context.FunctionCallIndex == context.FunctionCount - 1)
        {
            await _sink.EmitAsync(
                new RoundCompleted(context.Iteration + 1, _acc.TokenCount ?? 0),
                cancellationToken).ConfigureAwait(false);
        }

        return responseJson;
    }

    /// <summary>
    /// Shape the round's tool results as the upstream history: ONE tool-role message per result, in call
    /// order (the hand-rolled loop's shape; the base middleware would otherwise pack a round's results
    /// into a single tool message). The returned value IS the model-facing JSON the override produced -
    /// a refusal's synthetic JSON included - so the model reads a result for every call it made.
    /// </summary>
    protected override IList<ChatMessage> CreateResponseMessages(
        ReadOnlySpan<FunctionInvocationResult> results)
    {
        var messages = new List<ChatMessage>(results.Length);
        foreach (var result in results)
        {
            var content = result.Result as string ?? string.Empty;
            messages.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(result.CallContent.CallId, content)]));
        }

        return messages;
    }

    /// <summary>
    /// Run one entitled, in-budget tool call against its resolved <paramref name="entry"/>. The host is
    /// wrapped in <see cref="BudgetedToolHost"/> carrying the tool's effective <c>TokenBudget</c> and the
    /// per-call <see cref="ToolCardCollector"/> it reports side-effects to, and the call runs under its
    /// effective <c>CallTimeout</c> (Modal tools exempt; <c>&lt;= 0</c> disables). A timeout fails THIS
    /// call with a visible card error; the turn-lifetime token cancelling rethrows and ends the turn.
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        ToolEntry entry,
        FunctionCallContent call,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var tool = entry.Tool;

        // The per-call card: where the tool pushes its side-effects (citations/artifacts/stdout/todos)
        // instead of returning them; the outcome folds them onto the ExecutedToolCall below.
        var card = new ToolCardCollector();

        var invocation = new ToolInvocation
        {
            Pid = _request.Pid,
            ArgumentsJson = argumentsJson,
            // The artifact tools key/persist canvas artifacts on the conversation.
            ConversationId = _request.ConversationId,
            MessageId = _request.MessageId,
            ToolCallId = call.CallId,
            Deadline = _request.Host.Limits.Deadline,
            ClientTimezone = _request.ClientTimezone,
            // The sub-agent's provider + nested-entitlement ceiling: delegation talks to the turn's own
            // model and can never out-tool the caller.
            ModelId = _request.ModelId,
            AllowedToolIds = _toolset.AllowedToolIds,
        };

        var host = new BudgetedToolHost(_request.Host, entry.Effective.TokenBudget, card);

        // The generic per-call backstop: tools carry their own tighter limits (sandbox wall clock,
        // search timeouts); this catches a hang outside them. Modal tools (ask_user, sub_agent) are
        // exempt - blocking/long-running IS their job; their own Deadline math is the backstop and the
        // turn lifetime token remains the hard wall.
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
        return ToolOutcome.From(tool.Id, result, card, stopwatch.ElapsedMilliseconds);
    }

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
