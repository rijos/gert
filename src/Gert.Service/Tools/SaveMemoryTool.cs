using System.Text.Json;
using Gert.Model.Dtos;
using Gert.Service.Documents;
using Gert.Service.Validation;

namespace Gert.Service.Tools;

/// <summary>
/// The save-memory tool (chat-and-tools.md section save memory). Model function
/// <c>save_memory</c>: write ONE durable note into this project's memory via
/// <see cref="IMemoryService.UpsertAsync"/> - the same path the knowledge
/// panel's POST uses, so the entry is embedded into <c>rag.db</c> and
/// immediately retrievable by <c>search_documents</c>. The service owns user
/// context, clock, and validation; the tool only parses arguments and maps a
/// <see cref="ValidationException"/> to a model-correctable tool error.
/// <para>
/// There is deliberately no <c>pinned</c> argument: pinning injects the entry
/// into every future system prompt, and that stays a human decision in the
/// knowledge panel. And note the no-dedup semantics the description warns
/// about - every call creates a NEW entry (the DTO carries no id).
/// </para>
/// </summary>
public sealed class SaveMemoryTool : ITool
{
    private readonly IMemoryService _memory;

    public SaveMemoryTool(IMemoryService memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
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
            // Pinned = false always - see the class doc. The service validates
            // fail-closed (CreateMemoryRequestValidator: SafeText ShortTextMax /
            // LongTextMax) and embeds before persisting anything.
            var entry = await _memory.UpsertAsync(
                invocation.Pid,
                new CreateMemoryRequest { Title = title, Content = content, Pinned = false },
                cancellationToken).ConfigureAwait(false);

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
