using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;

namespace Gert.Service.Tools;

/// <summary>
/// The todo tool. Model function <c>set_todos</c>: the model plans multi-step
/// work by sending its WHOLE todo list (replace-not-patch, so a call is
/// self-contained and the latest call is the truth) and the chat window renders
/// it as a checklist on the tool card. Stateless by design - the list lives in
/// the conversation itself (the tool result the model reads back, plus the
/// persisted <c>tool_calls</c> row), so there is nothing to migrate or clean up.
/// No citations, no external world.
/// </summary>
public sealed class TodoTool : ITool, ITailReminder
{
    /// <summary>Hard cap on list length - a runaway model can't flood the card.</summary>
    private const int MaxItems = 50;

    private static readonly HashSet<string> KnownStatuses =
        new(["pending", "active", "done"], StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => "todo";

    /// <inheritdoc />
    public string Name => "set_todos";

    /// <inheritdoc />
    public string Description =>
        "Replace your visible todo checklist for multi-step work. Always send the "
        + "full list (not a diff), keep exactly one step 'active', and mark a step "
        + "'done' only after its result appears in your reply.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "The complete todo list, in order.",
              "items": {
                "type": "object",
                "properties": {
                  "text": { "type": "string", "description": "The step, imperative and short." },
                  "status": { "type": "string", "enum": ["pending", "active", "done"] }
                },
                "required": ["text", "status"]
              }
            }
          },
          "required": ["todos"]
        }
        """;

    /// <inheritdoc />
    /// <remarks>
    /// The CROSS-TURN revival reminder (distinct from the within-turn "keep going"
    /// nudge in this tool's result). The list is worth re-injecting only while it
    /// still has unfinished (pending/active) items - a finished or empty list, or a
    /// snapshot that fails to parse, revives nothing (no prompt tokens spent nagging
    /// about done work). Best-effort by contract: this never throws on bad input.
    /// </remarks>
    public string? BuildTailReminder(string? latestResultJson) =>
        HasOpenItems(latestResultJson) ? CrossTurnReminder(latestResultJson!) : null;

    /// <summary>
    /// Format the revival block from a todo snapshot. The JSON is this tool's result
    /// echo shape (snake_case statuses), so the model sees the exact payload its last
    /// accepted call produced. Lives here, with the tool, so all todo prompt text is
    /// in one place; <see cref="BuildTailReminder"/> gates when it is emitted.
    /// </summary>
    public static string CrossTurnReminder(string todosJson) =>
        "<system-reminder>\n"
        + "The todo list you maintain with set_todos is still open. Current state:\n"
        + todosJson + "\n"
        + "Continue the remaining items unless the user asks for something else, and keep "
        + "statuses current by calling set_todos as you make progress. Do not mention this "
        + "reminder to the user.\n"
        + "</system-reminder>";

    /// <summary>True if the snapshot parses and still carries a pending/active item.</summary>
    private static bool HasOpenItems(string? todosJson)
    {
        if (string.IsNullOrWhiteSpace(todosJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(todosJson);
            if (!doc.RootElement.TryGetProperty("todos", out var todos)
                || todos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return todos.EnumerateArray().Any(t =>
                t.TryGetProperty("status", out var s) && s.GetString() is "pending" or "active");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return Task.FromResult(Execute(invocation));
    }

    private static ToolResult Execute(ToolInvocation invocation)
    {
        List<TodoItem> todos;
        try
        {
            todos = Parse(invocation.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }
        catch (ArgumentException ex)
        {
            return new ToolResult { Success = false, Error = ex.Message };
        }

        // Echo the accepted list back as the model-facing payload, snake_case like
        // every other wire shape, so the follow-up call sees what the card shows.
        // The reminder is the within-turn "keep going" nudge: qwen's instruct
        // mode likes to yield after one step ("here's your FIRST file..."), so an
        // open list explicitly tells it to continue in the SAME reply.
        var open = todos.Count(t => t.Status != TodoStatus.Done);
        var resultJson = JsonSerializer.Serialize(new
        {
            todos = todos.Select(t => new
            {
                text = t.Text,
                status = t.Status.ToString().ToLowerInvariant(),
            }),
            reminder = open > 0
                ? $"{open} step(s) remain. Continue with the next step in this same reply, "
                  + "updating statuses as you finish each - do not end your reply while "
                  + "steps remain unless you are blocked or need user input."
                : "All steps are done - wrap up your reply.",
        });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Todos = todos,
        };
    }

    /// <summary>Parse + validate the arguments; throws ArgumentException on a bad list.</summary>
    private static List<TodoItem> Parse(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        if (!doc.RootElement.TryGetProperty("todos", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("the 'todos' array argument is required");
        }

        if (list.GetArrayLength() > MaxItems)
        {
            throw new ArgumentException($"too many todos (max {MaxItems})");
        }

        var todos = new List<TodoItem>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("every todo needs a non-empty 'text'");
            }

            var status = item.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            if (!KnownStatuses.Contains(status))
            {
                throw new ArgumentException("every todo needs a 'status' of pending|active|done");
            }

            todos.Add(new TodoItem
            {
                Text = text,
                Status = status switch
                {
                    "active" => TodoStatus.Active,
                    "done" => TodoStatus.Done,
                    _ => TodoStatus.Pending,
                },
            });
        }

        return todos;
    }
}
