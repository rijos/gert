using System.Text.Json;
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

    /// <summary>Tool id whose persisted snapshots the cross-turn reminder revives (TodoTool).</summary>
    private const string TodoToolId = "todo";

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
            Attachments = request.Attachments is { Count: > 0 } ? request.Attachments : null,
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
        // reasoning back upstream (Qwen3.6 interleaved thinking). Image
        // attachments ride user rows for vision-capable models only — a model
        // the catalog gates keeps the text but never sees image parts (mirror
        // of the tools gate; the prompt degrades, the turn never errors).
        var history = ToModelMessages(
            priorMessages.Where(m => m.Status == MessageStatus.Complete).Append(userMessage),
            includeReasoning: preserveThinking == true,
            includeImages: _catalog.SupportsVision(assistantMessage.ModelId!));

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

        // 6.5 Cross-turn todo revival: the history above is role+content only,
        // so a list the model set via set_todos in an earlier turn has already
        // vanished from the prompt. When the todo tool is offered this turn and
        // the latest accepted snapshot still has unfinished items, a reminder
        // carrying that snapshot rides at the TAIL of the rendered prompt
        // (appended to the new user message) — never the system prompt, whose
        // bytes must stay stable for the vLLM prefix cache, and never the
        // persisted user row, which keeps the user's actual words. Best-effort:
        // a broken read or unparseable snapshot must not fail the turn.
        if (offered.Any(t => t.Id == TodoToolId))
        {
            var todosJson = await ReadOpenTodosJsonAsync(repo, conversationId, cancellationToken)
                .ConfigureAwait(false);
            if (todosJson is not null)
            {
                history = AppendTodoReminder(history, todosJson);
            }
        }

        // 7. Per-model user defaults (the picker's cogwheel): conversation params
        // win field-by-field; settings are best-effort — a broken settings read
        // must not fail the turn.
        var modelParams = await ResolveModelParamsAsync(assistantMessage.ModelId!, cancellationToken)
            .ConfigureAwait(false);

        // 7.5 Mode-correct sampling: with thinking OFF, a model whose
        // generation_config.json only ships the thinking-mode set (Qwen3.6)
        // must get its declared instruct sampling explicitly — otherwise vLLM
        // fills omitted fields with the wrong mode's values (and without the
        // presence penalty the decode repetition-loops). Catalog-declared,
        // field-by-field, the LAST fallback: conversation and user settings
        // always win.
        var instruct = thinking == false
            ? _catalog.InstructParams(assistantMessage.ModelId!)
            : null;

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
            Temperature = conversation.Params.Temperature ?? modelParams?.Temperature ?? instruct?.Temperature,
            TopP = conversation.Params.TopP ?? modelParams?.TopP ?? instruct?.TopP,
            PresencePenalty = conversation.Params.PresencePenalty ?? modelParams?.PresencePenalty
                ?? instruct?.PresencePenalty,
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
        // The built-in canvas convention always rides first (real models don't
        // know the name= opt-in otherwise); project instructions append after.
        if (_instructions is null)
        {
            return SystemPrompts.Canvas;
        }

        try
        {
            var instructions = await _instructions
                .GetInstructionsAsync(_user.Iss, _user.Sub, pid, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(instructions)
                ? SystemPrompts.Canvas
                : SystemPrompts.Canvas + "\n\n" + instructions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort (step 0): a broken reader must not fail the turn.
            return SystemPrompts.Canvas;
        }
    }

    /// <summary>
    /// The latest accepted todo snapshot's JSON when it still has unfinished
    /// (pending/active) items, else null. A finished or empty list needs no
    /// revival — no prompt tokens spent nagging about done work. Best-effort:
    /// any read/parse failure means "no reminder", never a failed turn.
    /// </summary>
    private static async Task<string?> ReadOpenTodosJsonAsync(
        IChatRepository repo,
        string conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await repo.GetLatestToolCallAsync(conversationId, TodoToolId, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(latest?.ResponseJson))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(latest.ResponseJson);
            if (!doc.RootElement.TryGetProperty("todos", out var todos)
                || todos.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var hasOpenItems = todos.EnumerateArray().Any(t =>
                t.TryGetProperty("status", out var s) && s.GetString() is "pending" or "active");
            return hasOpenItems ? latest.ResponseJson : null;
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
    /// Re-render the tail user message with the todo reminder appended. The
    /// reminder exists ONLY in this turn's rendered prompt: prior turns keep
    /// their exact bytes (prefix-cache reuse up to the previous tail), and the
    /// persisted user row stays clean for the UI.
    /// </summary>
    private static IReadOnlyList<ChatModelMessage> AppendTodoReminder(
        IReadOnlyList<ChatModelMessage> history,
        string todosJson)
    {
        var rendered = history.ToList();
        var last = rendered[^1];
        rendered[^1] = last with
        {
            Content = last.Content + "\n\n" + SystemPrompts.TodoReminder(todosJson),
        };
        return rendered;
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
    /// With <paramref name="includeImages"/>, user rows carry their persisted
    /// image attachments as vision content parts.
    /// </summary>
    private static IReadOnlyList<ChatModelMessage> ToModelMessages(
        IEnumerable<Message> messages,
        bool includeReasoning = false,
        bool includeImages = true) =>
        messages.Select(m => new ChatModelMessage
        {
            Role = ToOpenAiRole(m.Role),
            Content = m.Content,
            Images = includeImages && m.Role == MessageRole.User && m.Attachments is { Count: > 0 }
                ? m.Attachments
                    .Select(a => new ChatModelImage { MimeType = a.MimeType, DataBase64 = a.Data })
                    .ToList()
                : null,
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
