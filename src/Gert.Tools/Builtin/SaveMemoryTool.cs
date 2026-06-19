using System.Text.Json;
using Gert.Model.Dtos;
using Gert.Service.Documents;
using Gert.Service.Tools;
using Gert.Service.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The save-memory tool (chat-and-tools.md section save memory). Model function
/// <c>save_memory</c> writes ONE durable note via <see cref="IMemoryService.UpsertAsync"/>
/// - the same path the knowledge panel's POST uses, so the entry is embedded into
/// <c>rag.db</c> and retrievable by <c>search_documents</c>. The service owns user
/// context, clock, and validation; the tool only parses args and maps a
/// <see cref="ValidationException"/> to a model-correctable tool error.
/// <para>
/// No <c>pinned</c> argument by design: pinning injects the entry into every future
/// system prompt, which stays a human decision in the knowledge panel. No dedup -
/// every call creates a NEW entry (the DTO carries no id).
/// </para>
/// </summary>
public sealed class SaveMemoryTool : ITool
{
    private readonly IMemoryService _memory;
    private readonly IValidationProvider _validation;

    public SaveMemoryTool(IMemoryService memory, IValidationProvider validation)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <inheritdoc />
    public string Id => "memory";

    /// <inheritdoc />
    public string Name => "save_memory";

    /// <inheritdoc />
    public string Description =>
        "Save one short, durable fact or preference the user states, for future "
        + "conversations in this project. Each call creates a new entry - never "
        + "re-save something you already saved this conversation.";

    /// <inheritdoc />
    public string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "A short label for the memory (a few words)." },
            "content": { "type": "string", "description": "The fact or preference to remember, in one or two sentences." }
          },
          "required": ["title", "content"]
        }
        """;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string? title;
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ToolResult { Success = false, Error = "invalid arguments: not a JSON object" };
            }

            title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return new ToolResult { Success = false, Error = "the 'title' argument is required" };
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ToolResult { Success = false, Error = "the 'content' argument is required" };
        }

        try
        {
            // Pinned = false always - see the class doc. The model's tool-call args are
            // untrusted, so the tool is the boundary: Prove validates fail-closed and
            // throws ValidationException (caught below as a model-correctable error).
            var request = _validation.Prove(
                new CreateMemoryRequest { Title = title, Content = content, Pinned = false });
            var entry = await _memory.UpsertAsync(invocation.Pid, request, cancellationToken)
                .ConfigureAwait(false);

            return new ToolResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(new { id = entry.Id, title = entry.Title, saved = true }),
                Stdout = $"saved memory: {entry.Title}",
            };
        }
        catch (ValidationException ex)
        {
            // Model-correctable (too long / unsafe text): the model can shorten
            // and retry. Any OTHER exception propagates to the runner's generic
            // per-call catch - card error, turn continues.
            return new ToolResult { Success = false, Error = ex.Message };
        }
    }
}
