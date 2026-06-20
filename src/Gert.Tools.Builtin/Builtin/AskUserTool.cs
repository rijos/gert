using System.Text.Json;
using Gert.Tools;

namespace Gert.Tools.Builtin;

/// <summary>
/// The ask-user tool (chat-and-tools.md section Ask the user). Model function
/// <c>ask_user</c>: show up to FOUR questions mid-turn (rendered as tabs) and
/// block until they are all answered, the wait times out, or the turn is
/// cancelled. The tool owns only the schema + caps and the answered/timeout
/// result shape; the human-interaction machinery (the question registry, the
/// wire events, the deadline budget) lives behind <see cref="IToolHost.Ui"/>
/// (the chat loop's <c>ChatToolUi</c>), so the tool depends on the
/// <see cref="IToolUi"/> contract, not the chat impl.
/// <para>
/// <see cref="ToolType.Modal"/> (via <see cref="ToolCallModal"/>) exempts the wait from the
/// generic <c>ToolCallTimeout</c>. A timeout is a SUCCESSFUL result the model continues from; a
/// host with no Ui (autonomous driver) fails the call closed. <see cref="RequiresHuman"/> keeps
/// the tool off an autonomous driver's advertised set.
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

    /// <inheritdoc />
    public override string Id => "ask_user";

    /// <inheritdoc />
    public override string Name => "ask_user";

    /// <inheritdoc />
    public override bool RequiresHuman => true;

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
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        // No human-interaction surface (autonomous driver): fail closed and
        // readable. RequiresHuman also keeps the tool off such a driver's
        // advertised set; this is the execution-time backstop.
        if (host.Ui is null)
        {
            return new ToolResult { Success = false, Error = "ask_user is not available in this context" };
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

        var prompts = parsed.Prompts!;
        var result = await host.Ui.AskAsync(new InteractionRequest(prompts), cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            // The Ui rejected the request (e.g. a question already pends) - a
            // model-correctable tool error, not a torn turn.
            return new ToolResult { Success = false, Error = result.Error };
        }

        if (!result.Answered)
        {
            // Timeout is an ordinary tool_result, no extra event; the model is
            // told to continue with its best judgement.
            return new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { answered = false, reason = "timeout" }),
                Stdout = "The user did not respond.",
            };
        }

        // Pair each prompt with its answer so the model has context for which
        // prompt each reply belongs to.
        var pairs = prompts
            .Select((p, i) => new { question = p.Text, answer = result.Answers[i] })
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

            var parsed = new List<InteractionPrompt>();
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

                parsed.Add(outcome.Prompt!);
            }

            if (parsed.Count == 0)
            {
                return ParsedArguments.Invalid("at least one question is required");
            }

            if (parsed.Count > MaxQuestions)
            {
                return ParsedArguments.Invalid($"too many questions (max {MaxQuestions})");
            }

            return ParsedArguments.Valid(parsed);
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

        return ParsedQuestion.Valid(new InteractionPrompt(question.Trim(), header, options, freeText));
    }

    private sealed record ParsedArguments(IReadOnlyList<InteractionPrompt>? Prompts, string? Error)
    {
        public static ParsedArguments Valid(IReadOnlyList<InteractionPrompt> prompts) => new(prompts, null);

        public static ParsedArguments Invalid(string error) => new(null, error);
    }

    private sealed record ParsedQuestion(InteractionPrompt? Prompt, string? Error)
    {
        public static ParsedQuestion Valid(InteractionPrompt prompt) => new(prompt, null);

        public static ParsedQuestion Invalid(string error) => new(null, error);
    }
}
