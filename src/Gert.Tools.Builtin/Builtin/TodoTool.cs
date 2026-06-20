using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// Model function <c>set_todos</c>: the model sends its WHOLE todo list
/// (replace-not-patch, so a call is self-contained and the latest call is the
/// truth). Stateless by design - the list lives in the conversation itself (the
/// tool result the model reads back, plus the persisted <c>tool_calls</c> row),
/// so there is nothing to migrate or clean up.
/// </summary>
public sealed class TodoTool : ToolCall<TodoArgs, TodoToolResult>, IToolReminder
{
    public TodoTool(IValidationProvider validation)
        : base(validation)
    {
    }

    /// <inheritdoc />
    public override string Id => "todo";

    /// <inheritdoc />
    public override string Name => "set_todos";

    /// <inheritdoc />
    public override string Description =>
        "Replace your visible todo checklist for multi-step work. Always send the "
        + "full list (not a diff), keep exactly one step 'active', and mark a step "
        + "'done' only after its result appears in your reply.";

    /// <inheritdoc />
    public override string ParametersSchema =>
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
    /// nudge in this tool's result). Re-injected only while the list still has
    /// pending/active items; a finished, empty, or unparseable snapshot revives
    /// nothing. Best-effort by contract: never throws on bad input.
    /// </remarks>
    public string? BuildTailReminder(string? latestResultJson) =>
        HasOpenItems(latestResultJson) ? CrossTurnReminder(latestResultJson!) : null;

    /// <summary>
    /// Format the revival block from a todo snapshot (this tool's result echo shape,
    /// snake_case statuses), so the model sees the exact payload its last accepted
    /// call produced. Lives here so all todo prompt text is in one place;
    /// <see cref="BuildTailReminder"/> gates when it is emitted.
    /// </summary>
    public static string CrossTurnReminder(string todosJson) =>
        "<system-reminder>\n"
        + "The todo list you maintain with set_todos is still open. Current state:\n"
        + todosJson + "\n"
        + "Continue the remaining items unless the user asks for something else, and keep "
        + "statuses current by calling set_todos as you make progress. Do not mention this "
        + "reminder to the user.\n"
        + "</system-reminder>";

    /// <inheritdoc />
    public override Task<ToolCallResult<TodoToolResult>> CallAsync(
        TodoArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        var todos = args.Todos
            .Select(t => new TodoItem { Text = t.Text, Status = ToStatus(t.Status) })
            .ToList();

        // Echo the accepted list back as the model-facing payload, snake_case like
        // every other wire shape, so the follow-up call sees what the card shows.
        // The reminder is the within-turn "keep going" nudge: qwen's instruct
        // mode likes to yield after one step ("here's your FIRST file..."), so an
        // open list explicitly tells it to continue in the SAME reply.
        var open = todos.Count(t => t.Status != TodoStatus.Done);
        var payload = new TodoToolResult
        {
            Todos = todos
                .Select(t => new TodoEcho { Text = t.Text, Status = t.Status.ToString().ToLowerInvariant() })
                .ToList(),
            Reminder = open > 0
                ? $"{open} step(s) remain. Continue with the next step in this same reply, "
                  + "updating statuses as you finish each - do not end your reply while "
                  + "steps remain unless you are blocked or need user input."
                : "All steps are done - wrap up your reply.",
        };

        return Task.FromResult(ToolCallResult<TodoToolResult>.Ok(payload, todos: todos));
    }

    /// <summary>Map a validated snake_case status onto the model <see cref="TodoStatus"/>.</summary>
    private static TodoStatus ToStatus(string status) => status switch
    {
        "active" => TodoStatus.Active,
        "done" => TodoStatus.Done,
        _ => TodoStatus.Pending,
    };

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
}
