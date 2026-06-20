using System.Text.Json;
using Gert.Model.Events;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Tools;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Builtin;

/// <summary>
/// The ask-user tool (chat-and-tools.md section Ask the user). Model function
/// <c>ask_user</c>: show up to FOUR questions mid-turn (rendered as tabs) and
/// block until they are all answered, the wait times out, or the turn is
/// cancelled. The questions travel as a <see cref="QuestionAskedEvent"/> through
/// the invocation's <see cref="ToolInvocation.EmitAsync"/> seam (persisted before
/// published, so a reconnecting client replays them); the answers arrive through
/// the <see cref="ITurnQuestions"/> registry from <c>POST .../answer</c>.
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal"/>) exempts the wait from the
/// generic <c>ToolCallTimeout</c>; the budget is
/// min(<see cref="TurnOptions.AskUserTimeout"/>, remaining turn budget -
/// <see cref="DeadlineGrace"/>) anchored on <see cref="ToolInvocation.Deadline"/>,
/// so the graceful "user did not respond" result always beats the turn-budget
/// error finalize. A timeout is a SUCCESSFUL result the model continues from; a
/// detached (client-gone) turn just times out and proceeds (detached-turn
/// guarantee). While the question pends the turn is in-flight, so the 409 rule
/// blocks new sends: the question card (or Stop) is the only input.
/// </para>
/// </summary>
public sealed class AskUserTool : ToolCallModal
{
    /// <summary>Max questions asked together in one call (rendered as tabs).</summary>
    public const int MaxQuestions = 4;

    /// <summary>Cap on the question text - a prompt, not an essay.</summary>
    public const int MaxQuestionChars = 2_000;

    /// <summary>Cap on a question's short tab label.</summary>
    public const int MaxHeaderChars = 60;

    /// <summary>Max closed answer choices per question (rendered as buttons).</summary>
    public const int MaxOptions = 8;

    /// <summary>Cap on one option's text.</summary>
    public const int MaxOptionChars = 200;

    /// <summary>
    /// Slice reserved off the turn deadline so the timeout result (emit + row +
    /// final round) lands before the runner's lifetime token fires.
    /// </summary>
    public static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(15);

    private readonly ITurnQuestions _questions;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly TurnOptions _options;

    public AskUserTool(
        ITurnQuestions questions,
        IUserContext user,
        TimeProvider time,
        IOptions<TurnOptions> options)
    {
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override string Id => "ask_user";

    /// <inheritdoc />
    public override string Name => "ask_user";

    /// <inheritdoc />
    public override string Description =>
        "Ask the user up to four clarifying questions at once and wait for their "
        + "answers (shown as tabs). Use only when you cannot proceed without input; "
        + "offer options when a choice is closed. If the result says they did not "
        + "respond, continue with your best judgement.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "description": "One to four questions to ask together, shown as tabs.",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string", "description": "The question to show the user." },
                  "header": { "type": "string", "description": "Optional short tab label (defaults to 'Question N')." },
                  "options": { "type": "array", "items": { "type": "string" },
                               "description": "Optional closed set of answer choices (max 8), rendered as buttons." },
                  "allow_free_text": { "type": "boolean",
                               "description": "Allow a typed answer in addition to options. Default true when no options are given, false otherwise." }
                },
                "required": ["question"]
              }
            }
          },
          "required": ["questions"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrEmpty(invocation.ConversationId))
        {
            return new ToolResult { Success = false, Error = "ask_user needs a conversation context" };
        }

        // Without the emit seam the question could never reach the user - wait
        // would block invisibly. A non-streaming host gets a readable error.
        if (invocation.EmitAsync is null || string.IsNullOrEmpty(invocation.ToolCallId))
        {
            return new ToolResult { Success = false, Error = "ask_user needs a streaming host" };
        }

        if (ParseArguments(invocation.ArgumentsJson) is not { } parsed)
        {
            return new ToolResult { Success = false, Error = "invalid arguments: not a JSON object" };
        }

        if (parsed.Error is not null)
        {
            // Model-correctable error, not a torn-down turn.
            return new ToolResult { Success = false, Error = parsed.Error };
        }

        var payload = parsed.Payload!;
        var key = new TurnKey(_user.Iss, _user.Sub, invocation.Pid, invocation.ConversationId);

        IPendingQuestion pending;
        try
        {
            pending = _questions.Open(key, payload);
        }
        catch (QuestionAlreadyPendingException ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }

        // The using guarantees the registry never leaks the key - answer,
        // timeout, cancel, or a fault below all release it.
        using (pending)
        {
            await invocation.EmitAsync(
                new QuestionAskedEvent
                {
                    Id = invocation.ToolCallId,
                    QuestionId = pending.QuestionId,
                    Questions = payload.Questions
                        .Select(q => new AskedQuestion(q.Question, q.Header, q.Options, q.AllowFreeText))
                        .ToList(),
                },
                cancellationToken).ConfigureAwait(false);

            // Effective wait: the knob, capped by what remains of the turn
            // budget minus the grace slice (floor zero -> immediate timeout).
            var wait = _options.AskUserTimeout;
            if (invocation.Deadline is { } deadline)
            {
                var remaining = deadline - _time.GetUtcNow() - DeadlineGrace;
                if (remaining < wait)
                {
                    wait = remaining;
                }
            }

            // A user cancel (or shutdown/turn budget) cancels the wait -> OCE ->
            // the runner's cancel/error finalize; the using above releases the key.
            var answers = await pending.WaitAsync(wait, cancellationToken).ConfigureAwait(false);

            if (answers is null)
            {
                // Timeout is an ordinary tool_result, no extra event; the model
                // is told to continue with its best judgement.
                return new ToolResult
                {
                    Success = true,
                    ResultJson = JsonSerializer.Serialize(new { answered = false, reason = "timeout" }),
                    Stdout = "The user did not respond.",
                };
            }

            await invocation.EmitAsync(
                new QuestionAnsweredEvent
                {
                    Id = invocation.ToolCallId,
                    QuestionId = pending.QuestionId,
                    Answers = answers,
                },
                cancellationToken).ConfigureAwait(false);

            // Pair each question with its answer so the model has context for
            // which prompt each reply belongs to.
            var pairs = payload.Questions
                .Select((q, i) => new { question = q.Question, answer = answers[i] })
                .ToList();

            return new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { answered = true, answers = pairs }),
                Stdout = string.Join(
                    "\n",
                    pairs.Select(p => $"{p.question} {p.answer}")),
            };
        }
    }

    private static ParsedArguments? ParseArguments(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return ParsedArguments.Invalid("questions must be an array of question objects");
            }

            var parsed = new List<QuestionItem>();
            foreach (var item in arr.EnumerateArray())
            {
                if (ParseQuestion(item) is not { } outcome)
                {
                    return ParsedArguments.Invalid("each question must be an object");
                }

                if (outcome.Error is not null)
                {
                    return ParsedArguments.Invalid(outcome.Error);
                }

                parsed.Add(outcome.Item!);
            }

            if (parsed.Count == 0)
            {
                return ParsedArguments.Invalid("at least one question is required");
            }

            if (parsed.Count > MaxQuestions)
            {
                return ParsedArguments.Invalid($"too many questions (max {MaxQuestions})");
            }

            return ParsedArguments.Valid(new QuestionPayload(parsed));
        }
        catch (JsonException ex)
        {
            return ParsedArguments.Invalid($"invalid arguments: {ex.Message}");
        }
    }

    private static ParsedQuestion? ParseQuestion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var question = item.TryGetProperty("question", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(question))
        {
            return ParsedQuestion.Invalid("question is required");
        }

        if (question.Length > MaxQuestionChars)
        {
            return ParsedQuestion.Invalid($"question is too long (max {MaxQuestionChars} characters)");
        }

        var header = item.TryGetProperty("header", out var h) ? h.GetString() : null;
        if (header is not null)
        {
            header = header.Trim();
            if (header.Length == 0)
            {
                header = null;
            }
            else if (header.Length > MaxHeaderChars)
            {
                return ParsedQuestion.Invalid($"header is too long (max {MaxHeaderChars} characters)");
            }
        }

        var options = new List<string>();
        if (item.TryGetProperty("options", out var opts))
        {
            if (opts.ValueKind != JsonValueKind.Array)
            {
                return ParsedQuestion.Invalid("options must be an array of strings");
            }

            foreach (var opt in opts.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(opt.GetString()))
                {
                    return ParsedQuestion.Invalid("options must be non-empty strings");
                }

                options.Add(opt.GetString()!);
            }
        }

        if (options.Count > MaxOptions)
        {
            return ParsedQuestion.Invalid($"too many options (max {MaxOptions})");
        }

        if (options.Any(o => o.Length > MaxOptionChars))
        {
            return ParsedQuestion.Invalid($"an option is too long (max {MaxOptionChars} characters)");
        }

        var allowFreeText = item.TryGetProperty("allow_free_text", out var aft)
                            && aft.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? aft.GetBoolean()
            : (bool?)null;

        // Default per the schema: free text is the norm for an open question,
        // off when a closed option set was offered.
        var freeText = allowFreeText ?? options.Count == 0;
        if (!freeText && options.Count == 0)
        {
            return ParsedQuestion.Invalid("allow_free_text=false requires options");
        }

        return ParsedQuestion.Valid(new QuestionItem(question.Trim(), header, options, freeText));
    }

    private sealed record ParsedArguments(QuestionPayload? Payload, string? Error)
    {
        public static ParsedArguments Valid(QuestionPayload payload) => new(payload, null);

        public static ParsedArguments Invalid(string error) => new(null, error);
    }

    private sealed record ParsedQuestion(QuestionItem? Item, string? Error)
    {
        public static ParsedQuestion Valid(QuestionItem item) => new(item, null);

        public static ParsedQuestion Invalid(string error) => new(null, error);
    }
}
