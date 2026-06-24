using Gert.Agent.Loop;
using Gert.Service.Chat;
using Gert.Tools;
using Gert.Tools.Hosting;
using Microsoft.Extensions.AI;

namespace Gert.Agent.Hosting;

/// <summary>
/// <see cref="IToolDelegate"/> over the SAME <see cref="IAgentLoop"/> the parent turn runs
/// (chat-and-tools.md section sub-agent): <c>run_sub_agent</c> hands it a task, it builds a FRESH
/// nested conversation (system prompt + the task/context user message - nothing of the parent
/// history), runs the loop against the turn's own provider, and returns only the final text.
///
/// <para>
/// The nested host is AUTONOMOUS (built by the driver): no <see cref="IToolUi"/> (no <c>ask_user</c>),
/// a no-op delegate (no recursion), and the delegable tool set is already intersected with the
/// parent's entitlement snapshot - the claim stays the ceiling two levels deep (auth.md). The loop's
/// own <c>MaxRounds</c> wind-down bounds a runaway nested loop; the partial final text returns rather
/// than a "did not converge" error, which the parent reads and answers around.
/// </para>
/// </summary>
internal sealed class ChatToolDelegate : IToolDelegate
{
    /// <summary>Nested rounds are a side quest, not a second turn - cap them well below the parent's.</summary>
    private const int MaxRounds = 16;

    private const int MaxTaskChars = 8_000;
    private const int MaxContextChars = 32_000;

    private const string SystemPrompt =
        "You are a sub-agent: a focused worker completing one delegated task. You cannot see "
        + "the conversation that delegated it - the task description is everything you know, "
        + "and your final message is the only thing reported back. Work the task to completion "
        + "(using your tools where they help), then reply with the final result only: no "
        + "preamble, no questions back, no offers of further help.";

    private readonly IAgentLoop _loop;
    private readonly IChatClient _model;
    private readonly string _modelId;
    private readonly IReadOnlyList<ITool> _delegableTools;
    private readonly IReadOnlySet<string> _allowedToolIds;
    private readonly IToolHost _nestedHost;
    private readonly int? _maxTokensPerRound;
    private readonly IReadOnlyDictionary<string, ToolBoundsOverride> _perTool;

    public ChatToolDelegate(
        IAgentLoop loop,
        IChatClient model,
        string modelId,
        IReadOnlyList<ITool> delegableTools,
        IReadOnlySet<string> allowedToolIds,
        IToolHost nestedHost,
        int? maxTokensPerRound,
        IReadOnlyDictionary<string, ToolBoundsOverride> perTool)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _delegableTools = delegableTools ?? throw new ArgumentNullException(nameof(delegableTools));
        _allowedToolIds = allowedToolIds ?? throw new ArgumentNullException(nameof(allowedToolIds));
        _nestedHost = nestedHost ?? throw new ArgumentNullException(nameof(nestedHost));
        _maxTokensPerRound = maxTokensPerRound;
        _perTool = perTool ?? throw new ArgumentNullException(nameof(perTool));
    }

    public async Task<DelegateResult> RunAsync(
        DelegateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var task = request.Task;
        if (string.IsNullOrWhiteSpace(task))
        {
            return new DelegateResult { Success = false, Error = "'task' is required" };
        }

        if (task.Length > MaxTaskChars || (request.Context?.Length ?? 0) > MaxContextChars)
        {
            return new DelegateResult { Success = false, Error = "task or context too long" };
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(
                ChatRole.User,
                string.IsNullOrWhiteSpace(request.Context)
                    ? task
                    : $"{task}\n\nContext:\n{request.Context}"),
        };

        // The nested tool view: the delegable tools (all offered) intersected with the parent's
        // entitlement, the same operator overrides, but with each effective CallTimeout forced to
        // zero - a non-modal nested tool keeps no per-call backstop (the turn lifetime token is the
        // hard wall regardless); call caps + token budgets still apply.
        var offeredIds = _delegableTools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var toolset = new Toolset(
            _delegableTools, offeredIds, _allowedToolIds, _perTool,
            adjustBounds: bounds => bounds with { CallTimeout = TimeSpan.Zero });

        // Autonomous: the nested loop emits into the discard sink (no log, no bus); only its
        // returned final text matters. The parent turn's lifetime token is the hard wall.
        var result = await _loop.RunAsync(
            new AgentLoopRequest
            {
                Messages = messages,
                Tools = toolset,
                ModelId = _modelId,
                Model = _model,
                Host = _nestedHost,
                // The sub-agent owns no project/conversation context beyond RAG scoping,
                // which the pre-scoped nested host carries; Pid rides each invocation only
                // for tools that read it (the project RAG resource ignores it).
                Pid = string.Empty,
                MaxRounds = MaxRounds,
                MaxTokensPerRound = _maxTokensPerRound,
            },
            NullAgentEventSink.Instance,
            cancellationToken).ConfigureAwait(false);

        // The loop's MaxRounds wind-down already bounds a runaway nested loop and
        // returns the streamed final text; surface that as success (the parent reads
        // it and answers around a thin result) rather than inventing a convergence flag.
        // Upstream rounds = tool rounds + the final answer round.
        return new DelegateResult { Success = true, Text = result.Content, Rounds = result.ToolRounds + 1 };
    }
}
