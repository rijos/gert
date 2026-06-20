using System.Text;
using System.Text.Json;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.Chat;
using Gert.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Builtin;

/// <summary>
/// The sub-agent tool (chat-and-tools.md section sub-agent). Model function
/// <c>run_sub_agent</c> delegates a self-contained task to a FRESH nested
/// conversation against the same provider; only the sub-agent's final text
/// returns, so the nested rounds never enter the parent history (a context-hungry
/// side quest costs one tool result, not the whole transcript).
///
/// <para>
/// Tools are a read-only subset (<see cref="DelegableToolIds"/>) intersected with
/// the parent turn's entitlement snapshot (<see cref="ToolInvocation.AllowedToolIds"/>):
/// the claim stays the ceiling (auth.md) two levels deep. It never gets
/// <c>run_sub_agent</c> itself, so delegation cannot recurse.
/// </para>
///
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal"/>) exempts the wait from the
/// generic <c>ToolCallTimeout</c> backstop (a delegated research task legitimately
/// outlives 60 s); the budget is <see cref="ToolInvocation.Deadline"/> minus a
/// grace slice, so the graceful "ran out of time" result always lands before
/// the turn's own error finalize.
/// </para>
/// </summary>
public sealed class SubAgentTool : ToolCallModal
{
    /// <summary>Read-only built-ins a sub-agent may use (never itself).</summary>
    private static readonly string[] DelegableToolIds = ["rag", "search", "fetch", "clock"];

    /// <summary>Nested rounds are a side quest, not a second turn - cap them well below the parent's.</summary>
    private const int MaxRounds = 16;

    private const int MaxTaskChars = 8_000;
    private const int MaxContextChars = 32_000;

    /// <summary>Returned early so the parent turn keeps the time to read the result and answer.</summary>
    private static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(10);

    private const string SystemPrompt =
        "You are a sub-agent: a focused worker completing one delegated task. You cannot see "
        + "the conversation that delegated it - the task description is everything you know, "
        + "and your final message is the only thing reported back. Work the task to completion "
        + "(using your tools where they help), then reply with the final result only: no "
        + "preamble, no questions back, no offers of further help.";

    // IServiceProvider, not IEnumerable<ITool>: this tool IS an ITool, so taking
    // the collection in the constructor would recurse the resolution. The nested
    // tool set is resolved lazily per execution from the same scope instead.
    private readonly IServiceProvider _services;
    private readonly IChatClientFactory _clients;
    private readonly TurnOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubAgentTool> _logger;

    public SubAgentTool(
        IServiceProvider services,
        IChatClientFactory clients,
        IOptions<TurnOptions> options,
        TimeProvider clock,
        ILogger<SubAgentTool> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "sub_agent";

    /// <inheritdoc />
    public override string Name => "run_sub_agent";

    // Lean on purpose: the tools region must stay under the format-adherence
    // budget (chat-and-tools.md section tool specs are a token budget).
    /// <inheritdoc />
    public override string Description =>
        "Delegate one self-contained task to a sub-agent and wait for its result. It cannot "
        + "see this conversation (pass everything it needs in task/context); it can search "
        + "docs and the web, fetch pages, and read the clock. Only its final answer returns - "
        + "use it when the intermediate work would crowd this conversation.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "The complete, self-contained task." },
            "context": { "type": "string", "description": "Optional background material the task needs." }
          },
          "required": ["task"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string? task;
        string? context;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            task = doc.RootElement.TryGetProperty("task", out var t) ? t.GetString() : null;
            context = doc.RootElement.TryGetProperty("context", out var c) ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Fail($"invalid arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return Fail("'task' is required");
        }

        if (task.Length > MaxTaskChars || (context?.Length ?? 0) > MaxContextChars)
        {
            return Fail("task or context too long");
        }

        if (string.IsNullOrEmpty(invocation.ModelId))
        {
            // A host outside the turn loop (no provider snapshot) can't delegate.
            return Fail("sub-agent is unavailable in this host");
        }

        // The model sees Names; entitlement intersects Ids: delegable AND the
        // parent turn's snapshot, so the sub-agent can never out-tool its caller.
        var allowed = invocation.AllowedToolIds ?? new HashSet<string>(StringComparer.Ordinal);
        var nestedTools = _services.GetServices<ITool>()
            .Where(t => DelegableToolIds.Contains(t.Id, StringComparer.Ordinal)
                        && allowed.Contains(t.Id))
            .ToList();
        var specs = nestedTools
            .Select(t => new ChatToolSpec
            {
                Name = t.Name,
                Description = t.Description,
                ParametersSchema = t.ParametersSchema,
            })
            .ToList();

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = SystemPrompt },
            new()
            {
                Role = "user",
                Content = string.IsNullOrWhiteSpace(context)
                    ? task
                    : $"{task}\n\nContext:\n{context}",
            },
        };

        // The wait budget: the turn deadline minus a grace slice (the parent
        // still has to read the result), under the parent token as hard wall.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (invocation.Deadline is { } deadline)
        {
            var remaining = deadline - DeadlineGrace - _clock.GetUtcNow();
            lifetime.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        var model = _clients.ForProvider(invocation.ModelId);
        var lastText = string.Empty;
        var rounds = 0;
        try
        {
            for (rounds = 1; rounds <= MaxRounds; rounds++)
            {
                var request = new ChatCompletionRequest
                {
                    ModelId = invocation.ModelId,
                    Messages = messages,
                    Tools = specs,
                    MaxTokens = _options.MaxTokensPerRound > 0 ? _options.MaxTokensPerRound : null,
                };

                var text = new StringBuilder();
                var calls = new List<ChatModelToolCall>();
                await foreach (var chunk in model.StreamAsync(request, lifetime.Token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                    {
                        text.Append(chunk.TextDelta);
                    }

                    if (chunk.ToolCall is not null)
                    {
                        calls.Add(chunk.ToolCall);
                    }
                }

                lastText = text.ToString();
                if (calls.Count == 0)
                {
                    return Succeed(lastText, rounds);
                }

                messages.Add(new ChatModelMessage
                {
                    Role = "assistant",
                    Content = lastText.Length > 0 ? lastText : null,
                    ToolCalls = calls,
                });

                foreach (var call in calls)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var resultJson = await ExecuteNestedAsync(nestedTools, call, invocation, lifetime.Token)
                        .ConfigureAwait(false);
                    messages.Add(new ChatModelMessage
                    {
                        Role = "tool",
                        Content = resultJson,
                        ToolCallId = call.Id,
                    });
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && lifetime.IsCancellationRequested)
        {
            // Our own deadline, not the turn dying: report gracefully so the
            // parent can still answer with what it has.
            _logger.LogWarning("Sub-agent ran out of turn budget after {Rounds} round(s).", rounds);
            return Fail("sub-agent ran out of time before finishing the task");
        }

        _logger.LogWarning("Sub-agent exhausted its round cap ({MaxRounds}).", MaxRounds);
        return Fail($"sub-agent did not converge within {MaxRounds} rounds");
    }

    private async Task<string> ExecuteNestedAsync(
        IReadOnlyList<ITool> nestedTools,
        ChatModelToolCall call,
        ToolInvocation parent,
        CancellationToken cancellationToken)
    {
        var tool = nestedTools.FirstOrDefault(t =>
            string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            return ErrorJson($"no tool named '{call.Name}' is available to the sub-agent");
        }

        var invocation = new ToolInvocation
        {
            Pid = parent.Pid,
            ArgumentsJson = call.ArgumentsJson,
            ConversationId = parent.ConversationId,
            // No MessageId/ToolCallId/EmitAsync: nested work is invisible to the
            // stream by design - compactness is the point. No ModelId either, so
            // a nested tool could never re-delegate even if it tried.
            Deadline = parent.Deadline,
            ClientTimezone = parent.ClientTimezone,
        };

        try
        {
            var result = await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            return result.Success
                ? result.ResultJson ?? "{}"
                : ErrorJson(result.Error ?? "tool failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A nested defect degrades to an error the sub-agent model can read;
            // the detail goes to the log only (style guide section 7).
            _logger.LogError(ex, "Sub-agent nested tool '{ToolId}' failed.", tool.Id);
            return ErrorJson("tool failed unexpectedly");
        }
    }

    private static ToolResult Succeed(string text, int rounds) => new()
    {
        Success = true,
        ResultJson = JsonSerializer.Serialize(new { result = text, rounds }),
        Stdout = text,
    };

    private static ToolResult Fail(string error) => new() { Success = false, Error = error };

    private static string ErrorJson(string error) =>
        JsonSerializer.Serialize(new { error });
}
