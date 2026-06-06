using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;

namespace Gert.Service.Tools;

/// <summary>
/// The todo tool. Model function <c>set_todos</c>: the model plans multi-step
/// work by sending its WHOLE todo list (replace-not-patch, so a call is
/// self-contained and the latest call is the truth) and the chat window renders
/// it as a checklist on the tool card. Stateless by design — the list lives in
/// the conversation itself (the tool result the model reads back, plus the
/// persisted <c>tool_calls</c> row), so there is nothing to migrate or clean up.
/// No citations, no external world.
/// </summary>
public sealed class TodoTool : ITool
{
    /// <summary>Hard cap on list length — a runaway model can't flood the card.</summary>
    private const int MaxItems = 50;

    private static readonly HashSet<string> KnownStatuses =
        new(["pending", "active", "done"], StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => "todo";

    /// <inheritdoc />
    public string Name => "set_todos";

    /// <inheritdoc />
    public string Description =>
        "Replace your visible todo list for this conversation. Send the FULL list every "
        + "time (not a diff) with one entry per step. Keep exactly ONE step 'active' — the "
        + "one you are doing right now — and mark a step 'done' only AFTER its result "
        + "already appears in your reply, never in advance. Use it to plan and track "
        + "multi-step work.";

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
        // mode likes to yield after one step ("here's your FIRST file…"), so an
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
                  + "updating statuses as you finish each — do not end your reply while "
                  + "steps remain unless you are blocked or need user input."
                : "All steps are done — wrap up your reply.",
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
