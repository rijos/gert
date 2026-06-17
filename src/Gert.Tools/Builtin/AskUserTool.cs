using System.Text.Json;
using Gert.Model.Events;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Service.Tools;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Builtin;

/// <summary>
/// The ask-user tool (chat-and-tools.md section Ask the user). Model function
/// <c>ask_user</c>: show the user ONE question mid-turn and block until they
/// answer, the wait times out, or the turn is cancelled. The question travels
/// as a <see cref="QuestionAskedEvent"/> through the invocation's
/// <see cref="ToolInvocation.EmitAsync"/> seam (persisted before published, so
/// a reconnecting client replays it); the answer arrives through the
/// <see cref="ITurnQuestions"/> registry from <c>POST .../answer</c>.
/// <para>
/// <see cref="IInteractiveTool"/> exempts the wait from the generic
/// <c>ToolCallTimeout</c>; the budget here is
/// min(<see cref="TurnOptions.AskUserTimeout"/>, remaining turn budget -
/// <see cref="DeadlineGrace"/>), anchored on
/// <see cref="ToolInvocation.Deadline"/> - so the graceful "user did not
/// respond" result always beats the turn-budget error finalize. A timeout is a
/// SUCCESSFUL result (<c>{"answered":false,"reason":"timeout"}</c>) the model
/// continues from; on a detached (client-gone) turn the question simply times
/// out and the turn goes on - the detached-turn guarantee holds. While the
/// question pends the turn is in-flight, so the 409 rule blocks new sends in
/// the conversation: the question card (or Stop) is the only input.
/// </para>
/// </summary>
public sealed class AskUserTool : ITool, IInteractiveTool
{
    /// <summary>Cap on the question text - a prompt, not an essay.</summary>
    public const int MaxQuestionChars = 2_000;

    /// <summary>Max closed answer choices (rendered as buttons).</summary>
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
    public string Id => "ask_user";

    /// <inheritdoc />
    public string Name => "ask_user";

    /// <inheritdoc />
    public string Description =>
        "Ask the user one clarifying question and wait for their answer. Use only "
        + "when you cannot proceed without input; offer options when the choice is "
        + "closed. If the result says they did not respond, continue with your "
        + "best judgement.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question to show the user." },
            "options": { "type": "array", "items": { "type": "string" },
                         "description": "Optional closed set of answer choices (max 8), rendered as buttons." },
            "allow_free_text": { "type": "boolean",
                         "description": "Allow a typed answer in addition to options. Default true when no options are given, false otherwise." }
          },
          "required": ["question"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
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
            // Model-correctable errors (ClockTool style) - never a torn-down turn.
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
                    Question = payload.Question,
                    Options = payload.Options,
                    AllowFreeText = payload.AllowFreeText,
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
            var answer = await pending.WaitAsync(wait, cancellationToken).ConfigureAwait(false);

            if (answer is null)
            {
                // Timeout is the ordinary tool_result - no extra event; the
                // model is told to continue with its best judgement.
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
                    Answer = answer,
                },
                cancellationToken).ConfigureAwait(false);

            return new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { answered = true, answer }),
                Stdout = answer,
            };
        }
    }

    private static ParsedArguments? ParseArguments(string argumentsJson)
    {
        string? question;
        var options = new List<string>();
        bool? allowFreeText;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            question = root.TryGetProperty("question", out var q) ? q.GetString() : null;

            if (root.TryGetProperty("options", out var opts))
            {
                if (opts.ValueKind != JsonValueKind.Array)
                {
                    return ParsedArguments.Invalid("options must be an array of strings");
                }

                foreach (var item in opts.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        return ParsedArguments.Invalid("options must be non-empty strings");
                    }

                    options.Add(item.GetString()!);
                }
            }

            allowFreeText = root.TryGetProperty("allow_free_text", out var aft)
                            && aft.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? aft.GetBoolean()
                : null;
        }
        catch (JsonException ex)
        {
            return ParsedArguments.Invalid($"invalid arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return ParsedArguments.Invalid("question is required");
        }

        if (question.Length > MaxQuestionChars)
        {
            return ParsedArguments.Invalid($"question is too long (max {MaxQuestionChars} characters)");
        }

        if (options.Count > MaxOptions)
        {
            return ParsedArguments.Invalid($"too many options (max {MaxOptions})");
        }

        if (options.Any(o => o.Length > MaxOptionChars))
        {
            return ParsedArguments.Invalid($"an option is too long (max {MaxOptionChars} characters)");
        }

        // Default per the schema: free text is the norm for an open question,
        // off when a closed option set was offered.
        var freeText = allowFreeText ?? options.Count == 0;
        if (!freeText && options.Count == 0)
        {
            return ParsedArguments.Invalid("allow_free_text=false requires options");
        }

        return ParsedArguments.Valid(new QuestionPayload(question.Trim(), options, freeText));
    }

    private sealed record ParsedArguments(QuestionPayload? Payload, string? Error)
    {
        public static ParsedArguments Valid(QuestionPayload payload) => new(payload, null);

        public static ParsedArguments Invalid(string error) => new(null, error);
    }
}
