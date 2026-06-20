using Gert.Model.Dtos;
using Gert.Service.Documents;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The save-memory tool (chat-and-tools.md section save memory). Model function
/// <c>save_memory</c> writes ONE durable note via <see cref="IMemoryService.UpsertAsync"/>
/// - the same path the knowledge panel's POST uses, so the entry is embedded into
/// <c>rag.db</c> and retrievable by <c>search_documents</c>. The service owns user
/// context, clock, and validation; the tool maps the args onto a
/// <see cref="CreateMemoryRequest"/> and re-proves THAT (the authoritative caps),
/// mapping a <see cref="ValidationException"/> to a model-correctable tool error.
/// <para>
/// The arg validator (<c>SaveMemoryArgsValidator</c>) is presence-only by design:
/// the real length/safe-text bar is the <see cref="CreateMemoryRequest"/> validator,
/// proven below - so the two never impose conflicting caps.
/// </para>
/// <para>
/// No <c>pinned</c> argument by design: pinning injects the entry into every future
/// system prompt, which stays a human decision in the knowledge panel. No dedup -
/// every call creates a NEW entry (the DTO carries no id).
/// </para>
/// </summary>
public sealed class SaveMemoryTool : ToolCall<SaveMemoryArgs, SaveMemoryResult>
{
    private readonly IMemoryService _memory;
    private readonly IValidationProvider _validation;

    public SaveMemoryTool(IValidationProvider validation, IMemoryService memory)
        : base(validation)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _validation = validation;
    }

    /// <inheritdoc />
    public override string Id => "memory";

    /// <inheritdoc />
    public override string Name => "save_memory";

    /// <inheritdoc />
    public override string Description =>
        "Save one short, durable fact or preference the user states, for future "
        + "conversations in this project. Each call creates a new entry - never "
        + "re-save something you already saved this conversation.";

    /// <inheritdoc />
    public override string ParametersSchema =>
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
    public override async Task<ToolCallResult<SaveMemoryResult>> CallAsync(
        SaveMemoryArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            // Pinned = false always - see the class doc. The args passed presence
            // validation; this re-proves the authoritative CreateMemoryRequest bar
            // (length / safe-text) fail-closed and throws ValidationException
            // (caught below as a model-correctable error).
            var request = _validation.Prove(
                new CreateMemoryRequest { Title = args.Title, Content = args.Content, Pinned = false });
            var entry = await _memory.UpsertAsync(invocation.Pid, request, cancellationToken)
                .ConfigureAwait(false);

            var payload = new SaveMemoryResult { Id = entry.Id, Title = entry.Title, Saved = true };
            return ToolCallResult<SaveMemoryResult>.Ok(payload, stdout: $"saved memory: {entry.Title}");
        }
        catch (ValidationException ex)
        {
            // Model-correctable (too long / unsafe text): the model can shorten
            // and retry. Any OTHER exception propagates to the runner's generic
            // per-call catch - card error, turn continues.
            return ToolCallResult<SaveMemoryResult>.Fail(ex.Message);
        }
    }
}
