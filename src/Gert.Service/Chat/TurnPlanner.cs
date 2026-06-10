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
/// the <c>streaming</c> assistant placeholder are persisted with allocated seqs
/// in one gated transaction (the <c>ux_messages_streaming</c> index is the
/// atomic 409 protection — decisions §11), and the identity + entitlement
/// snapshot is captured into the <see cref="TurnJob"/>. No DB handle survives
/// the call (open-per-use).
/// </summary>
public sealed class TurnPlanner : ITurnPlanner
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly IValidationProvider _validation;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IProjectInstructionsReader? _instructions;
    private readonly IModelCatalog _catalog;
    private readonly ISettingsService? _settings;
    private readonly TurnOptions _options;
    private readonly TimeProvider _time;

    public TurnPlanner(
        IChatDatabaseProvider databases,
        IUserContext user,
        IValidationProvider validation,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options,
        TimeProvider time,
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
        _time = time ?? throw new ArgumentNullException(nameof(time));
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
            .OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        var conversation = await repo.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        // 2. Serialize turns per conversation (the 409 rule): a second turn while
        // one is streaming would race the seq counter and read incomplete history.
        // The ux_messages_streaming INDEX is the truth (the gated insert below);
        // this read is an optimization — it rejects before allocating seqs, with
        // orphan-aware semantics — and the write-back trigger, never the
        // protection.
        var priorMessages = conversation is null
            ? []
            : await repo.ListMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Injected clock (dotnet-style-guide.md §5): tests pin the instant, so the
        // orphan-horizon / 409 rules are deterministic.
        var now = _time.GetUtcNow();
        if (priorMessages.Any(m => MessageStatusRules.IsTurnInProgress(m, now, _options.MaxTurnDuration)))
        {
            throw new TurnInProgressException(conversationId);
        }

        // 2.5 Orphan write-back: every streaming row left at this point is one
        // the fast path above just proved expired (it didn't throw, so
        // MessageStatusRules.Effective maps it to error) — make that durable so
        // the dead turn's row frees the gate index. Without this the partial
        // unique index would turn the self-healing lazy orphan rule into a
        // permanent lock. A false return means the runner or a racing planner
        // finalized it first — both fine. Note the queued-job subtlety: a job
        // that expired while still queued in a backed-up lane runs after this
        // write-back already freed its gate; its runner sees remaining <= 0,
        // cancels immediately, and its best-effort error finalize re-writes a
        // row that is already error — idempotent, harmless.
        foreach (var orphan in priorMessages.Where(m => m.Status == MessageStatus.Streaming))
        {
            _ = await repo.TryExpireStreamingMessageAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
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
                ModelId = request.ModelId ?? ModelInfo.DefaultId,
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
        // the 409 rule, and the orphan rule observe while the worker runs. The
        // two rows go in ONE gated transaction (below): the placeholder insert
        // hitting ux_messages_streaming is the atomic 409 protection, so a
        // losing racer persists no message rows at all. Its two seq bumps do
        // leave a gap in next_seq — harmless and deliberate (seq is an ordering
        // cursor, not dense); conversation materialisation above is also fine
        // (the winner needed it anyway, and INSERT OR IGNORE makes that race
        // benign).
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

        var assistantSeq = await repo.AllocateSeqAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var assistantMessage = new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelId = request.ModelId ?? conversation.ModelId ?? ModelInfo.DefaultId,
            TokenCount = null,
            Seq = assistantSeq,
            Status = MessageStatus.Streaming,
            CreatedAt = now,
        };

        if (!await repo.TryInsertTurnMessagesAsync(userMessage, assistantMessage, cancellationToken)
                .ConfigureAwait(false))
        {
            // Lost the race after the fast-path check: another plan holds the gate.
            throw new TurnInProgressException(conversationId);
        }

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

        // 6.5 Cross-turn state revival: the history above is role+content only, so
        // state a tool set in an earlier turn has already vanished from the prompt.
        // Every offered tool that implements ITailReminder gets its newest accepted
        // result snapshot and decides whether to re-inject it; any reminder it returns
        // rides at the TAIL of the rendered prompt (appended to the new user message)
        // — never the system prompt, whose bytes must stay stable for the vLLM prefix
        // cache, and never the persisted user row, which keeps the user's actual words.
        // Best-effort throughout: a broken read or a tool's parse bug must not fail the
        // turn (the todo list is the one reviver today).
        foreach (var tool in offered)
        {
            if (tool is not ITailReminder reviver)
            {
                continue;
            }

            var snapshot = await ReadLatestToolResultAsync(repo, conversationId, tool.Id, cancellationToken)
                .ConfigureAwait(false);
            var reminder = TryBuildTailReminder(reviver, snapshot);
            if (!string.IsNullOrEmpty(reminder))
            {
                history = AppendTailReminder(history, reminder);
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
            // The shared anchor (one clock read, not two): the SAME instant
            // that stamped the placeholder's CreatedAt, so the runner's
            // remaining-budget cap and the readers' orphan/409 horizon can
            // never disagree (see TurnJob.PlannedAt).
            PlannedAt = now,
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
    /// The <c>ResponseJson</c> of a tool's newest accepted (<c>done</c>) call, or
    /// null when there is none. The raw snapshot a <see cref="ITailReminder"/> turns
    /// into a reminder — the planner reads it; the tool interprets it. Best-effort:
    /// any read failure means "no snapshot", never a failed turn.
    /// </summary>
    private static async Task<string?> ReadLatestToolResultAsync(
        IChatRepository repo,
        string conversationId,
        string toolId,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await repo.GetLatestToolCallAsync(conversationId, toolId, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(latest?.ResponseJson) ? null : latest.ResponseJson;
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
    /// Ask a reviver for its tail reminder, swallowing any fault. The interface
    /// contracts that implementations don't throw, but a parse bug must never fail
    /// the turn — so the planner guards the call too.
    /// </summary>
    private static string? TryBuildTailReminder(ITailReminder reviver, string? snapshot)
    {
        try
        {
            return reviver.BuildTailReminder(snapshot);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-render the tail user message with a tool's revival reminder appended. The
    /// reminder exists ONLY in this turn's rendered prompt: prior turns keep their
    /// exact bytes (prefix-cache reuse up to the previous tail), and the persisted
    /// user row stays clean for the UI.
    /// </summary>
    private static IReadOnlyList<ChatModelMessage> AppendTailReminder(
        IReadOnlyList<ChatModelMessage> history,
        string reminder)
    {
        var rendered = history.ToList();
        var last = rendered[^1];
        rendered[^1] = last with
        {
            Content = last.Content + "\n\n" + reminder,
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

    /// <summary>The title cap in UTF-16 code units; cuts land on grapheme boundaries.</summary>
    private const int MaxTitleLength = 60;

    // Seed a conversation title from its first message (single-lined, capped).
    private static string DeriveTitle(string content)
    {
        var text = (content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0)
        {
            return "New chat";
        }

        if (text.Length <= MaxTitleLength)
        {
            return text;
        }

        // Cut on a grapheme (text-element) boundary so the cap can never split a
        // surrogate pair (emoji) or strand combining marks — a naive text[..60]
        // could end on a lone high surrogate, which is invalid UTF-16.
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var end = 0;
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + ((string)elements.Current).Length;
            if (next > MaxTitleLength)
            {
                break;
            }

            end = next;
        }

        // end == 0 only for a pathological 60+-unit first grapheme: degrade to the
        // placeholder rather than emit broken UTF-16.
        return end > 0 ? text[..end] : "New chat";
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
