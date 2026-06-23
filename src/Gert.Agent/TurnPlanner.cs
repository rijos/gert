using Gert.Chat;
using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Tools;
using Gert.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gert.Agent;

/// <summary>
/// <see cref="ITurnPlanner"/> - phase 1 in the request scope (chat-and-tools.md
/// section detached turns). Order: validation throws before any disk touch
/// (principles.md #6); the concurrent-turn check throws before any write; then the
/// user message and the <c>streaming</c> assistant placeholder persist with allocated
/// seqs in one gated transaction (the <c>ux_messages_streaming</c> index is the atomic
/// 409 protection - decisions section 11); the identity + entitlement snapshot is
/// captured into the <see cref="TurnJob"/>. No DB handle survives the call (open-per-use).
/// </summary>
public sealed class TurnPlanner : ITurnPlanner
{
    private readonly IChatDatabaseProvider _databases;
    private readonly IUserContext _user;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IProjectInstructionsReader? _instructions;
    private readonly IChatProviderCatalog _catalog;
    private readonly TurnOptions _options;
    private readonly PromptOptions _prompts;
    private readonly TimeProvider _time;
    private readonly ILogger<TurnPlanner> _logger;

    public TurnPlanner(
        IChatDatabaseProvider databases,
        IUserContext user,
        IEnumerable<ITool> tools,
        IOptions<TurnOptions> options,
        IOptions<PromptOptions> prompts,
        TimeProvider time,
        IProjectInstructionsReader? instructions,
        IChatProviderCatalog? catalog = null,
        ILogger<TurnPlanner>? logger = null)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _prompts = prompts?.Value ?? throw new ArgumentNullException(nameof(prompts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _instructions = instructions;
        _catalog = catalog ?? new NullChatProviderCatalog();
        _logger = logger ?? NullLogger<TurnPlanner>.Instance;
    }

    /// <inheritdoc />
    public async Task<TurnJob> PlanAsync(
        string pid,
        string conversationId,
        Validated<SendMessageRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken)
            .ConfigureAwait(false);

        var conversation = await repo.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        // 2. Serialize turns per conversation (the 409 rule): a second turn while
        // one is streaming would race the seq counter and read incomplete history.
        // The ux_messages_streaming INDEX is the truth (the gated insert below);
        // this read is an optimization - it rejects before allocating seqs, with
        // orphan-aware semantics - and the write-back trigger, never the
        // protection.
        var priorMessages = conversation is null
            ? []
            : await repo.ListMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Injected clock (dotnet-style-guide.md section 5): tests pin the instant, so the
        // orphan-horizon / 409 rules are deterministic.
        var now = _time.GetUtcNow();
        if (priorMessages.Any(m => MessageStatusRules.IsTurnInProgress(m, now, _options.MaxTurnDuration)))
        {
            throw new TurnInProgressException(conversationId);
        }

        // 2.5 Orphan write-back: every streaming row left here is one the fast path
        // just proved expired (it didn't throw, so MessageStatusRules.Effective maps
        // it to error) - make that durable so the dead turn's row frees the gate index.
        // Without this the partial unique index turns the self-healing lazy orphan rule
        // into a permanent lock. A false return means the runner or a racing planner
        // finalized it first - both fine. Queued-job subtlety: a job that expired while
        // still queued in a backed-up lane runs after this write-back freed its gate;
        // its runner sees remaining <= 0, cancels, and its best-effort error finalize
        // re-writes an already-error row - idempotent, harmless.
        foreach (var orphan in priorMessages.Where(m => m.Status == MessageStatus.Streaming))
        {
            _ = await repo.TryExpireStreamingMessageAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
        }

        // 3. First message to a not-yet-created conversation materialises it (the
        // SPA's "new chat -> type -> send" sends a fresh client id). Title seeds
        // from the first message.
        if (conversation is null)
        {
            conversation = new Conversation
            {
                Id = conversationId,
                Title = DeriveTitle(dto.Content),
                ModelId = dto.ModelId ?? ChatProviderInfo.DefaultId,
                Tools = dto.Tools ?? new ToolToggles(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.InsertConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
        else if (dto.Tools is not null && !dto.Tools.Equals(conversation.Tools))
        {
            // Tool toggles ride each send; a changed set persists onto the
            // conversation: ResolveOfferedTools intersects with the conversation
            // set, so without this write-back a tool toggled ON mid-conversation
            // would stay vetoed by the stale snapshot from materialise forever
            // (off->on was impossible).
            conversation = conversation with
            {
                Tools = dto.Tools,
                UpdatedAt = now,
            };
            await repo.UpdateConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        }

        // 4. Persist the user message (complete) and the assistant placeholder
        // (streaming) with allocated seqs - the placeholder is what readers, the 409
        // rule, and the orphan rule observe while the worker runs. The two rows go in
        // ONE gated transaction (below): the placeholder insert hitting
        // ux_messages_streaming is the atomic 409 protection, so a losing racer persists
        // no message rows at all. Its two seq bumps leave a gap in next_seq - harmless
        // and deliberate (seq is an ordering cursor, not dense); conversation
        // materialisation above is also fine (the winner needed it anyway, and
        // INSERT OR IGNORE makes that race benign).
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = dto.Content,
            Attachments = dto.Attachments is { Count: > 0 } ? dto.Attachments : null,
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
            ModelId = dto.ModelId ?? conversation.ModelId ?? ChatProviderInfo.DefaultId,
            TokenCount = null,
            Seq = assistantSeq,
            Status = MessageStatus.Streaming,
            CreatedAt = now,
        };

        // Bound an inline TEXT-file attachment against the model's context window: a file too big
        // to fit (leaving room for the prompt + reply) is refused here with a clean 400 that steers
        // the user to the Knowledge panel (RAG) instead - fail-closed, before any row is persisted.
        // Images are excluded (their own count/size caps apply); a provider with an unknown context
        // (the synthesized zero-config default) is not gated.
        EnsureInlineAttachmentsFit(dto.Attachments, assistantMessage.ModelId!);

        if (!await repo.TryInsertTurnMessagesAsync(userMessage, assistantMessage, cancellationToken)
                .ConfigureAwait(false))
        {
            // Lost the race after the fast-path check: another plan holds the gate.
            throw new TurnInProgressException(conversationId);
        }

        // 5. History for the model: prior complete turns + the new user message.
        // Streaming/error/cancelled rows never enter the prompt (an error or
        // user-stopped row's partial content is a UI artifact, not conversation
        // truth - and a stable history maximises vLLM prefix-cache reuse).
        // Assistant rows always carry their persisted reasoning into history; the
        // adapter forwards it upstream only when the selected provider has
        // preserve_thinking on (Qwen3.6 interleaved thinking) - gated there, not
        // here. Image attachments ride user rows for vision-capable providers only
        // - a provider the catalog gates keeps the text but never sees image parts
        // (mirror of the tools gate; the prompt degrades, the turn never errors).
        var history = ToModelMessages(
            priorMessages.Where(m => m.Status == MessageStatus.Complete).Append(userMessage),
            includeImages: _catalog.SupportsVision(assistantMessage.ModelId!));

        // Step 0: the project's pinned instructions (best-effort; a missing reader
        // or project means "no instructions", never a failed turn).
        var systemPrompt = await ResolveSystemPromptAsync(pid, cancellationToken).ConfigureAwait(false);

        // 6. The offered tool set: requested AND conversation-enabled AND entitlement
        // AND registry (auth.md section the claim is the ceiling) AND MODEL CAPABILITY -
        // a model the catalog marks as not tool-capable is never advertised
        // tools, whatever the toggles say. Plus the entitlement SNAPSHOT for
        // the off-thread execution-time re-check.
        var offered = _catalog.SupportsTools(assistantMessage.ModelId!)
            ? ResolveOfferedTools(dto, conversation)
            : Array.Empty<ITool>();

        // 6.5 Cross-turn state revival: the history above is role+content only, so
        // state a tool set in an earlier turn has already vanished from the prompt.
        // Every offered tool that implements IToolReminder gets its newest accepted
        // result snapshot and decides whether to re-inject it; any reminder it returns
        // rides at the TAIL of the rendered prompt (appended to the new user message)
        // - never the system prompt, whose bytes must stay stable for the vLLM prefix
        // cache, and never the persisted user row, which keeps the user's actual words.
        // Best-effort throughout: a broken read or a tool's parse bug must not fail the
        // turn (the todo list is the one reviver today).
        foreach (var tool in offered)
        {
            if (tool is not IToolReminder reviver)
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

        // 7. Hand the prepared turn off. Sampling + the thinking template kwargs are
        // not resolved here - they ride the selected provider (ModelId), applied by
        // the adapter from Gert:Chat:Providers. The job carries only what the runner needs.
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
            SystemPrompt = systemPrompt,
            ClientTimezone = dto.Timezone,
        };
    }

    /// <summary>
    /// Requested AND conversation-enabled AND entitled AND registered, in
    /// registry order -- the entitlement is the hard ceiling (auth.md).
    /// </summary>
    private IReadOnlyList<ITool> ResolveOfferedTools(SendMessageRequest request, Conversation? conversation)
    {
        var requested = request.Tools?.EnabledIds
                        ?? conversation?.Tools.EnabledIds
                        ?? new HashSet<string>(StringComparer.Ordinal);

        var conversationEnabled = conversation?.Tools.EnabledIds;

        var offered = new List<ITool>();
        var entitlementDropped = 0;
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
                entitlementDropped++;
                continue;
            }

            offered.Add(tool);
        }

        // The fail-closed floor is deliberate (auth.md section the claim is the ceiling),
        // but reaching it silently turns "search the web" into a model with no
        // tools that improvises pseudo-call syntax - make the drop visible so a
        // missing/misconfigured gert_tools claim is a log line, not a mystery.
        if (offered.Count == 0 && entitlementDropped > 0)
        {
            _logger.LogWarning(
                "All {Dropped} requested tool(s) were dropped by the gert_tools entitlement (token grants {Granted}) - the model receives no tools this turn.",
                entitlementDropped, _user.AllowedTools.Count);
        }

        return offered;
    }

    private async Task<string?> ResolveSystemPromptAsync(string pid, CancellationToken cancellationToken)
    {
        // The operator-configured canvas convention (Gert:Prompts:Canvas) rides
        // first (real models don't know the name= opt-in otherwise); project
        // instructions append after. An empty canvas is omitted.
        var canvas = _prompts.Canvas;
        if (_instructions is null)
        {
            return Combine(canvas, null);
        }

        try
        {
            var instructions = await _instructions
                .GetInstructionsAsync(_user.Iss, _user.Sub, pid, cancellationToken)
                .ConfigureAwait(false);
            return Combine(canvas, instructions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort (step 0): a broken reader returns just the canvas (or
            // null when the canvas is also empty - the runner skips a null prompt).
            return Combine(canvas, null);
        }
    }

    // canvas + instructions -> "canvas\n\ninstructions"; either alone -> that one;
    // neither -> null (TurnRunner skips an empty/null system prompt).
    private static string? Combine(string? canvas, string? instructions)
    {
        var hasCanvas = !string.IsNullOrWhiteSpace(canvas);
        var hasInstructions = !string.IsNullOrWhiteSpace(instructions);
        return (hasCanvas, hasInstructions) switch
        {
            (true, true) => canvas + "\n\n" + instructions,
            (true, false) => canvas,
            (false, true) => instructions,
            _ => null,
        };
    }

    /// <summary>
    /// The <c>ResponseJson</c> of a tool's newest accepted (<c>done</c>) call, or
    /// null when there is none. The raw snapshot a <see cref="IToolReminder"/> turns
    /// into a reminder - the planner reads it; the tool interprets it. Best-effort:
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
    /// the turn - so the planner guards the call too.
    /// </summary>
    private static string? TryBuildTailReminder(IToolReminder reviver, string? snapshot)
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
    /// user row stays clean for the UI. Any image parts on the tail message are
    /// preserved; only its text grows.
    /// </summary>
    private static IReadOnlyList<ChatMessage> AppendTailReminder(
        IReadOnlyList<ChatMessage> history,
        string reminder)
    {
        var rendered = history.ToList();
        var last = rendered[^1];
        var contents = new List<AIContent> { new TextContent(last.Text + "\n\n" + reminder) };
        contents.AddRange(last.Contents.Where(c => c is not TextContent));
        rendered[^1] = new ChatMessage(last.Role, contents);
        return rendered;
    }

    /// <summary>
    /// Map persisted chat rows to the Microsoft.Extensions.AI upstream message list. Assistant
    /// rows always carry their persisted thinking as a <see cref="TextReasoningContent"/>; the
    /// adapter forwards it upstream as <c>reasoning_content</c> only when the selected provider has
    /// preserve_thinking on (the OpenAIProviderChatClient gates it; an instruct provider's adapter drops
    /// it). With <paramref name="includeImages"/>, user rows carry their persisted image attachments
    /// as <see cref="DataContent"/> vision parts.
    /// </summary>
    private static IReadOnlyList<ChatMessage> ToModelMessages(
        IEnumerable<Message> messages,
        bool includeImages = true) =>
        messages.Select(m => ToChatMessage(m, includeImages)).ToList();

    private static ChatMessage ToChatMessage(Message m, bool includeImages)
    {
        var role = ToChatRole(m.Role);
        var content = m.Content ?? string.Empty;

        // User attachments: the text part (when present), then one part per attachment. An image
        // becomes a vision DataContent (only for vision-capable models; otherwise dropped). A
        // text-file attachment is decoded and injected as a fenced block - no vision needed, so it
        // rides regardless of model capability; non-text bytes are skipped (the model just doesn't
        // see them). The adapter renders each DataContent as an OpenAI image_url base64 data URL.
        if (m.Role == MessageRole.User && m.Attachments is { Count: > 0 } attachments)
        {
            var parts = new List<AIContent>();
            if (!string.IsNullOrEmpty(content))
            {
                parts.Add(new TextContent(content));
            }

            foreach (var attachment in attachments)
            {
                if (AttachmentKinds.IsImage(attachment.MimeType))
                {
                    if (includeImages)
                    {
                        parts.Add(new DataContent(Convert.FromBase64String(attachment.Data), attachment.MimeType));
                    }
                }
                else if (Gert.Service.Ingestion.TextContent.TryDecode(
                    Convert.FromBase64String(attachment.Data), out var fileText))
                {
                    parts.Add(new TextContent(FormatFileAttachment(attachment.Name, fileText)));
                }
            }

            // Collapse back to a plain string when nothing structured remains (e.g. a non-vision
            // model with only image attachments, or all attachments unreadable).
            if (parts.Count == 0)
            {
                return new ChatMessage(role, content);
            }

            if (parts is [TextContent single])
            {
                return new ChatMessage(role, single.Text);
            }

            return new ChatMessage(role, parts);
        }

        if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.Reasoning))
        {
            return new ChatMessage(role, [new TextReasoningContent(m.Reasoning), new TextContent(content)]);
        }

        return new ChatMessage(role, content);
    }

    /// <summary>
    /// Render a text-file attachment as a fenced block the model reads as data: a header naming the
    /// file (so a user can say "this json file") and the content in a code fence. The text is
    /// untrusted document content, not instructions - the fence + header frame it as such.
    /// </summary>
    private static string FormatFileAttachment(string? name, string text)
    {
        var header = string.IsNullOrEmpty(name) ? "Attached file:" : $"Attached file `{name}`:";
        return $"{header}\n\n```\n{text}\n```";
    }

    /// <summary>
    /// Refuse an inline text-file attachment that would not fit the model's context (leaving room
    /// for the prompt + reply): a <see cref="ValidationException"/> -> 400 steering the user to the
    /// Knowledge panel. Images are excluded (their own caps apply); when the provider declares no
    /// context (the zero-config default) there is nothing to gate against, so it passes.
    /// </summary>
    private void EnsureInlineAttachmentsFit(IReadOnlyList<MessageAttachment>? attachments, string modelId)
    {
        if (attachments is not { Count: > 0 } || _catalog.ContextSize(modelId) is not (int contextTokens and > 0))
        {
            return;
        }

        var budget = (long)(contextTokens * _options.MaxInlineAttachmentContextFraction);
        var inlineTokens = attachments
            .Where(a => !AttachmentKinds.IsImage(a.MimeType))
            .Sum(a => EstimateBase64TextTokens(a.Data));

        if (inlineTokens <= budget)
        {
            return;
        }

        throw new ValidationException(ValidationResult.Failure(
        [
            new ValidationError
            {
                Property = "attachments",
                Message =
                    $"That file is too large to attach here (~{inlineTokens / 1000}k tokens; the inline "
                    + $"limit is ~{budget / 1000}k of this model's {contextTokens / 1000}k context). Add it to "
                    + "the Knowledge panel instead and I'll search or read it for you.",
                Code = "attachment.too_large_for_context",
            },
        ]));
    }

    /// <summary>
    /// Rough token estimate for base64-encoded UTF-8 text: base64 decodes ~3 bytes per 4 chars, and
    /// ~4 chars per token (the repo has no tokenizer; this is the budget brake, not an exact count).
    /// </summary>
    private static long EstimateBase64TextTokens(string base64) => (long)base64.Length * 3 / 16;

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
        // surrogate pair (emoji) or strand combining marks - a naive text[..60]
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

    private static ChatRole ToChatRole(MessageRole role) => role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };
}
