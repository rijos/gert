using System.Runtime.CompilerServices;
using System.Text;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Validation;

namespace Gert.Service.Chat;

/// <summary>
/// The chat orchestrator (chat-and-tools.md § the tool loop), walking-skeleton
/// slice: the <b>no-tool path only</b>, split into two stateless phases.
/// <list type="bullet">
///   <item>
///     <see cref="StartTurnAsync"/> (phase 1): validate the request
///     (principles.md #6 — input is the boundary; throws
///     <see cref="ValidationException"/> on failure, before any disk touch),
///     open <c>chat.db</c>, persist the user <see cref="Message"/>, load prior
///     turns, and build the in-memory <see cref="ChatTurn"/>.
///   </item>
///   <item>
///     <see cref="RunAsync"/> (phase 2): re-open <c>chat.db</c>, call the model
///     streaming, yield <c>message_start → delta* → message_end</c>, and persist
///     the assistant <see cref="Message"/> (+ token count). A model error
///     mid-stream yields an <see cref="ErrorEvent"/> and persists nothing more.
///   </item>
/// </list>
/// No DB handle is held across the two phases (open-per-use), and no turn state is
/// cached server-side — everything phase 2 needs is captured in the
/// <see cref="ChatTurn"/>, so GERT runs safely as multiple instances.
/// Tools / citations / artifacts / pinned memory land in U7b.
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly IDatabaseProvider _databases;
    private readonly IChatModelClient _model;
    private readonly IUserContext _user;
    private readonly IValidationProvider _validation;

    /// <summary>Fallback model id when neither the request nor conversation supplies one.</summary>
    private const string DefaultModelId = "default";

    public ChatService(
        IDatabaseProvider databases,
        IChatModelClient model,
        IUserContext user,
        IValidationProvider validation)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <inheritdoc />
    public async Task<ChatTurn> StartTurnAsync(
        string pid,
        string conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Validate (fail-closed at the service boundary). Reject before any disk
        // touch by throwing — the host maps ValidationException to a 400.
        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        // Open the project's chat repository for the *current user* — identity is
        // never caller-supplied (configuration.md § 2.5).
        await using var repo = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        var conversation = await repo.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        // 2. Persist the user message.
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = request.Content,
            ModelId = null,
            TokenCount = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await repo.InsertMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);

        // 3. Load prior turns (trim-to-context-window deferred — chat-and-tools.md step 1).
        var priorMessages = await repo.ListMessagesAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        var modelId = request.ModelId
                      ?? conversation?.ModelId
                      ?? DefaultModelId;

        // Pre-generate the assistant id so message_start (phase 2) and the persisted
        // assistant row share one id. The repo is closed when this method returns;
        // RunAsync re-opens it per-use (no handle crosses the phase boundary).
        return new ChatTurn
        {
            Pid = pid,
            ConversationId = conversationId,
            AssistantMessageId = Guid.NewGuid().ToString("D"),
            ModelId = modelId,
            Messages = ToModelMessages(priorMessages),
            // TODO U7b: resolve enabled tool ids (entitlement ∩ conversation toggles ∩ request).
            ToolIds = [],
            Temperature = conversation?.Params.Temperature,
            TopP = conversation?.Params.TopP,
            MaxTokens = conversation?.Params.MaxTokens,
            Stop = conversation?.Params.Stop,
            Seed = conversation?.Params.Seed,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatEvent> RunAsync(
        ChatTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var completion = new ChatCompletionRequest
        {
            ModelId = turn.ModelId,
            Messages = turn.Messages,
            // TODO U7b: advertise tools here (from turn.ToolIds), and prepend pinned
            // instructions/memory to a system message (step 0).
            Temperature = turn.Temperature,
            TopP = turn.TopP,
            MaxTokens = turn.MaxTokens,
            Stop = turn.Stop,
            Seed = turn.Seed,
        };

        // Stream the model, collecting deltas for persistence as we go.
        //
        // C# forbids a try/catch *around* a `yield return`, so we drive the
        // enumerator by hand: only the MoveNextAsync call is wrapped in the
        // try (no yield inside it), and a caught fault is surfaced as an
        // ErrorEvent yielded *after* the try. This keeps the model-error →
        // ErrorEvent path inside the iterator without swallowing it.
        yield return new MessageStartEvent { MessageId = turn.AssistantMessageId };

        var content = new StringBuilder();
        int? tokenCount = null;
        var faulted = false;

        await using var stream = _model
            .StreamAsync(completion, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool moved;
            ErrorEvent? error = null;
            try
            {
                moved = await stream.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the caller's intent — propagate, don't mask as an error event.
                throw;
            }
            catch (Exception ex)
            {
                error = new ErrorEvent { Message = ex.Message };
                moved = false;
            }

            if (error is not null)
            {
                faulted = true;
                yield return error;
                break;
            }

            if (!moved)
            {
                break;
            }

            var chunk = stream.Current;

            // TODO U7b: a chunk with ToolCall set drives the tool loop
            // (emit tool_call → execute → tool_result → re-enter step 2).
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                content.Append(chunk.TextDelta);
                yield return new DeltaEvent { Text = chunk.TextDelta };
            }

            if (chunk.TokenCount is not null)
            {
                tokenCount = chunk.TokenCount;
            }
        }

        if (faulted)
        {
            // The turn failed mid-stream: surface the error, persist nothing more.
            yield break;
        }

        // Persist the assistant message (+ token count), then close the turn. The
        // repo is re-opened per-use here — no handle was held across StartTurnAsync.
        // TODO U7b: persist citations + artifacts extracted from the content here.
        await using var repo = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, turn.Pid, cancellationToken)
            .ConfigureAwait(false);

        var assistantMessage = new Message
        {
            Id = turn.AssistantMessageId,
            ConversationId = turn.ConversationId,
            Role = MessageRole.Assistant,
            Content = content.ToString(),
            ModelId = turn.ModelId,
            TokenCount = tokenCount,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await repo.InsertMessageAsync(assistantMessage, cancellationToken).ConfigureAwait(false);

        yield return new MessageEndEvent { TokenCount = tokenCount };
    }

    /// <summary>Map persisted chat rows to the OpenAI-style upstream message list.</summary>
    private static IReadOnlyList<ChatModelMessage> ToModelMessages(IReadOnlyList<Message> messages)
    {
        var result = new List<ChatModelMessage>(messages.Count);
        foreach (var m in messages)
        {
            result.Add(new ChatModelMessage
            {
                Role = ToOpenAiRole(m.Role),
                Content = m.Content,
            });
        }

        return result;
    }

    private static string ToOpenAiRole(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System => "system",
        MessageRole.Tool => "tool",
        _ => "user",
    };
}
