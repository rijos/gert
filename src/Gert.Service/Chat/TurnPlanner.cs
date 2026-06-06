using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Database;
using Gert.Service.External;
using Gert.Service.Projects;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Microsoft.Extensions.Options;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="ITurnPlanner"/> — phase 1 in the request scope (chat-and-tools.md
/// § detached turns). Validation throws before any disk touch (principles.md #6);
/// the concurrent-turn check throws before any write; then the user message and
/// the <c>streaming</c> assistant placeholder are persisted with allocated seqs,
/// and the identity + entitlement snapshot is captured into the
/// <see cref="TurnJob"/>. No DB handle survives the call (open-per-use).
/// </summary>
public sealed class TurnPlanner : ITurnPlanner
{
    /// <summary>Fallback model id when neither the request nor conversation supplies one.</summary>
    private const string DefaultModelId = "default";

    private readonly IDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly IValidationProvider _validation;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IProjectInstructionsReader? _instructions;
    private readonly IModelCatalog _catalog;
    private readonly ISettingsService? _settings;
    private readonly TurnOptions _options;

    public TurnPlanner(
        IDatabaseProvider databases,
        IUserContext user,
        IValidationProvider validation,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options,
        IProjectInstructionsReader? instructions,
        IModelCatalog? catalog = null,
        ISettingsService? settings = null)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _instructions = instructions;
        _catalog = catalog ?? new NullModelCatalog();
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<TurnJob> PlanAsync(
        string pid,
        string conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Validate (fail-closed at the service boundary, before any disk touch).
        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        await using var repo = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        var conversation = await repo.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        // 2. Serialize turns per conversation (the 409 rule): a second turn while
        // one is streaming would race the seq counter and read incomplete history.
        // The orphan rule keeps a crashed worker from blocking forever.
        var priorMessages = conversation is null
            ? []
            : await repo.ListMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        if (priorMessages.Any(m => MessageStatusRules.IsTurnInProgress(m, now, _options.MaxTurnDuration)))
        {
            throw new TurnInProgressException(conversationId);
        }

        // 3. First message to a not-yet-created conversation materialises it (the
        // SPA's "new chat → type → send" sends a fresh client id). Title seeds
        // from the first message.
        if (conversation is null)
        {
            conversation = new Conversation
            {
                Id = conversationId,
                Title = DeriveTitle(request.Content),
                ModelId = request.ModelId ?? DefaultModelId,
                Tools = request.Tools ?? new ToolToggles(),
                Thinking = request.Thinking,
                PreserveThinking = request.PreserveThinking,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.InsertConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
        else if ((request.Thinking is not null && request.Thinking != conversation.Thinking)
                 || (request.PreserveThinking is not null && request.PreserveThinking != conversation.PreserveThinking))
        {
            // The composer toggles ride each send; a changed value persists onto
            // the conversation (parity with Tools at materialise) so a reload
            // restores the toggle state.
            conversation = conversation with
            {
                Thinking = request.Thinking ?? conversation.Thinking,
                PreserveThinking = request.PreserveThinking ?? conversation.PreserveThinking,
                UpdatedAt = now,
            };
            await repo.UpdateConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        }

        // 4. Persist the user message (complete) and the assistant placeholder
        // (streaming) with allocated seqs — the placeholder is what readers,
        // the 409 rule, and the orphan rule observe while the worker runs.
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = request.Content,
            ModelId = null,
            TokenCount = null,
            Seq = await repo.AllocateSeqAsync(conversationId, cancellationToken).ConfigureAwait(false),
            Status = MessageStatus.Complete,
            CreatedAt = now,
        };
        await repo.InsertMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);

        var assistantSeq = await repo.AllocateSeqAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var assistantMessage = new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelId = request.ModelId ?? conversation.ModelId ?? DefaultModelId,
            TokenCount = null,
            Seq = assistantSeq,
            Status = MessageStatus.Streaming,
            CreatedAt = now,
        };
        await repo.InsertMessageAsync(assistantMessage, cancellationToken).ConfigureAwait(false);

        // Effective reasoning toggles: per-request override, else the persisted
        // conversation preference, else null (= model/template default).
        var thinking = request.Thinking ?? conversation.Thinking;
        var preserveThinking = request.PreserveThinking ?? conversation.PreserveThinking;

        // 5. History for the model: prior complete turns + the new user message.
        // Streaming/error/cancelled rows never enter the prompt (an error or
        // user-stopped row's partial content is a UI artifact, not conversation
        // truth — and a stable history maximises vLLM prefix-cache reuse).
        // With preserve_thinking on, assistant rows carry their persisted
        // reasoning back upstream (Qwen3.6 interleaved thinking).
        var history = ToModelMessages(
            priorMessages.Where(m => m.Status == MessageStatus.Complete).Append(userMessage),
            includeReasoning: preserveThinking == true);

        // Step 0: the project's pinned instructions (best-effort; a missing reader
        // or project means "no instructions", never a failed turn).
        var systemPrompt = await ResolveSystemPromptAsync(pid, cancellationToken).ConfigureAwait(false);

        // 6. The offered tool set: requested ∩ conversation-enabled ∩ entitlement
        // ∩ registry (auth.md § the claim is the ceiling) ∩ MODEL CAPABILITY —
        // a model the catalog marks as not tool-capable is never advertised
        // tools, whatever the toggles say. Plus the entitlement SNAPSHOT for
        // the off-thread execution-time re-check.
        var offered = _catalog.SupportsTools(assistantMessage.ModelId!)
            ? ResolveOfferedTools(request, conversation)
            : Array.Empty<ITool>();

        // 7. Per-model user defaults (the picker's cogwheel): conversation params
        // win field-by-field; settings are best-effort — a broken settings read
        // must not fail the turn.
        var modelParams = await ResolveModelParamsAsync(assistantMessage.ModelId!, cancellationToken)
            .ConfigureAwait(false);

        return new TurnJob
        {
            Iss = _user.Iss,
            Sub = _user.Sub,
            Username = _user.Username,
            IsAdmin = _user.IsAdmin,
            AllowedToolIds = _user.AllowedTools.ToHashSet(StringComparer.Ordinal),
            Pid = pid,
            ConversationId = conversationId,
            UserMessageId = userMessage.Id,
            AssistantMessageId = assistantMessage.Id,
            AssistantSeq = assistantSeq,
            ModelId = assistantMessage.ModelId!,
            History = history,
            ToolIds = offered.Select(t => t.Id).ToList(),
            Tools = offered.Select(ToSpec).ToList(),
            SystemPrompt = systemPrompt,
            Thinking = thinking,
            PreserveThinking = preserveThinking,
            Temperature = conversation.Params.Temperature ?? modelParams?.Temperature,
            TopP = conversation.Params.TopP ?? modelParams?.TopP,
            MaxTokens = conversation.Params.MaxTokens ?? modelParams?.MaxTokens,
            Stop = conversation.Params.Stop ?? modelParams?.Stop,
            Seed = conversation.Params.Seed ?? modelParams?.Seed,
        };
    }

    /// <summary>
    /// The user's per-model generation defaults for <paramref name="modelId"/>,
    /// or null. Best-effort: settings are preferences, never a turn-blocker.
    /// </summary>
    private async Task<GenerationParams?> ResolveModelParamsAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return null;
        }

        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            return settings.ModelParams is not null
                   && settings.ModelParams.TryGetValue(modelId, out var modelParams)
                ? modelParams
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Requested ∩ conversation-enabled ∩ entitlement ∩ registry, in registry
    /// order — the entitlement is the hard ceiling (auth.md). Lifted verbatim
    /// from the old ChatService.
    /// </summary>
    private IReadOnlyList<ITool> ResolveOfferedTools(SendMessageRequest request, Conversation? conversation)
    {
        var requested = request.Tools?.EnabledIds
                        ?? conversation?.Tools.EnabledIds
                        ?? new HashSet<string>(StringComparer.Ordinal);

        var conversationEnabled = conversation?.Tools.EnabledIds;

        var offered = new List<ITool>();
        foreach (var tool in _tools)
        {
            if (!requested.Contains(tool.Id))
            {
                continue;
            }

            if (conversationEnabled is not null && !conversationEnabled.Contains(tool.Id))
            {
                continue;
            }

            // HARD ceiling: the JWT entitlement. Dropped even if requested + enabled.
            if (!_user.CanUseTool(tool.Id))
            {
                continue;
            }

            offered.Add(tool);
        }

        return offered;
    }

    private async Task<string?> ResolveSystemPromptAsync(string pid, CancellationToken cancellationToken)
    {
        if (_instructions is null)
        {
            return null;
        }

        try
        {
            var instructions = await _instructions
                .GetInstructionsAsync(_user.Iss, _user.Sub, pid, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(instructions) ? null : instructions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort (step 0): a broken reader must not fail the turn.
            return null;
        }
    }

    private static ChatToolSpec ToSpec(ITool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        ParametersSchema = tool.ParametersSchema,
    };

    /// <summary>
    /// Map persisted chat rows to the OpenAI-style upstream message list. With
    /// <paramref name="includeReasoning"/>, assistant rows carry their persisted
    /// thinking as <c>reasoning_content</c> (preserve_thinking interleaving).
    /// </summary>
    private static IReadOnlyList<ChatModelMessage> ToModelMessages(
        IEnumerable<Message> messages,
        bool includeReasoning = false) =>
        messages.Select(m => new ChatModelMessage
        {
            Role = ToOpenAiRole(m.Role),
            Content = m.Content,
            ReasoningContent = includeReasoning && m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.Reasoning)
                ? m.Reasoning
                : null,
        }).ToList();

    // Seed a conversation title from its first message (single-lined, capped).
    private static string DeriveTitle(string content)
    {
        var text = (content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0)
        {
            return "New chat";
        }

        return text.Length > 60 ? text[..60] : text;
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
