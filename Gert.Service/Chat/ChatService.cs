using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Service.Validation;

namespace Gert.Service.Chat;

/// <summary>
/// The chat orchestrator (chat-and-tools.md § the tool loop), split into two
/// stateless phases the host drives within one HTTP request.
/// <list type="bullet">
///   <item>
///     <see cref="StartTurnAsync"/> (phase 1): validate the request
///     (principles.md #6 — input is the boundary; throws
///     <see cref="ValidationException"/> before any disk touch), open
///     <c>chat.db</c>, persist the user <see cref="Message"/>, load prior turns,
///     resolve the <b>offered tool set</b> (the entitlement intersection,
///     auth.md § the claim is the ceiling) and the project's pinned instructions
///     (step 0), and build the in-memory <see cref="ChatTurn"/>.
///   </item>
///   <item>
///     <see cref="RunAsync"/> (phase 2): advertise the offered tools, run the
///     tool loop — <c>tool_call → execute → tool_result → feed back → loop</c>
///     (capped) — then stream <c>delta</c>s, emit citations, and persist the
///     assistant message, its <c>tool_calls</c>, and citations.
///   </item>
/// </list>
/// No DB handle is held across the two phases (open-per-use), and no turn state is
/// cached server-side — everything phase 2 needs is captured in the
/// <see cref="ChatTurn"/>, so GERT runs safely as multiple instances.
/// </summary>
public sealed class ChatService : IChatService
{
    /// <summary>Fallback model id when neither the request nor conversation supplies one.</summary>
    private const string DefaultModelId = "default";

    /// <summary>Hard cap on tool rounds per turn, to bound a runaway tool loop.</summary>
    private const int MaxToolRounds = 5;

    private readonly IDatabaseProvider _databases;
    private readonly IChatModelClient _model;
    private readonly IUserContext _user;
    private readonly IValidationProvider _validation;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IProjectInstructionsReader? _instructions;

    public ChatService(
        IDatabaseProvider databases,
        IChatModelClient model,
        IUserContext user,
        IValidationProvider validation,
        IEnumerable<ITool> tools,
        IProjectInstructionsReader? instructions)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _instructions = instructions;
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

        // Step 0: prepend the project's pinned instructions (best-effort; a missing
        // reader or project means "no instructions", never a failed turn).
        var systemPrompt = await ResolveSystemPromptAsync(pid, cancellationToken).ConfigureAwait(false);

        // Resolve the offered tool set: requested ∩ conversation-enabled ∩
        // entitlement ∩ registry (auth.md § the claim is the ceiling).
        var offered = ResolveOfferedTools(request, conversation);

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
            ToolIds = offered.Select(t => t.Id).ToList(),
            Tools = offered.Select(ToSpec).ToList(),
            SystemPrompt = systemPrompt,
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

        yield return new MessageStartEvent { MessageId = turn.AssistantMessageId };

        // The growing conversation sent upstream: optional system prompt (step 0),
        // then prior turns, then any tool-call/tool-result pairs we append as the
        // loop runs.
        var messages = new List<ChatModelMessage>(turn.Messages.Count + 4);
        if (!string.IsNullOrWhiteSpace(turn.SystemPrompt))
        {
            messages.Add(new ChatModelMessage { Role = "system", Content = turn.SystemPrompt });
        }

        messages.AddRange(turn.Messages);

        var toolSpecs = turn.Tools;

        var content = new StringBuilder();
        int? tokenCount = null;
        var collectedCitations = new List<Citation>();
        var collectedToolCalls = new List<ToolCall>();
        var faulted = false;
        ErrorEvent? terminalError = null;

        var round = 0;
        while (true)
        {
            var completion = new ChatCompletionRequest
            {
                ModelId = turn.ModelId,
                Messages = messages,
                Tools = toolSpecs,
                Temperature = turn.Temperature,
                TopP = turn.TopP,
                MaxTokens = turn.MaxTokens,
                Stop = turn.Stop,
                Seed = turn.Seed,
            };

            // Drain one model call: collect text deltas and any tool calls. C# forbids
            // try/catch around a yield, so we buffer this call's events into a list and
            // yield them after the try; a model fault becomes a terminal ErrorEvent.
            var step = await DrainModelCallAsync(completion, cancellationToken).ConfigureAwait(false);

            // Surface this call's text deltas to the client (and accumulate for persistence).
            foreach (var delta in step.Deltas)
            {
                content.Append(delta);
                yield return new DeltaEvent { Text = delta };
            }

            if (step.TokenCount is not null)
            {
                tokenCount = step.TokenCount;
            }

            if (step.Error is not null)
            {
                faulted = true;
                terminalError = step.Error;
                break;
            }

            // No tool calls → the model produced its final answer; leave the loop.
            if (step.ToolCalls.Count == 0)
            {
                break;
            }

            // Round cap: stop offering tools and let the model answer with what it has.
            if (round >= MaxToolRounds)
            {
                toolSpecs = [];
                continue;
            }

            round++;

            // Execute each requested tool call, feeding the result back as a tool message.
            foreach (var call in step.ToolCalls)
            {
                // The assistant turn that asked for the tool must precede its result
                // in the upstream history.
                messages.Add(new ChatModelMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCallId = call.Id,
                });

                yield return new ToolCallEvent
                {
                    Id = call.Id,
                    Kind = ResolveKind(call.Name),
                    Status = ToolCallStatus.Running,
                    Request = ParseArgs(call.ArgumentsJson),
                };

                var outcome = await ExecuteToolAsync(turn.Pid, call, cancellationToken).ConfigureAwait(false);

                yield return new ToolResultEvent
                {
                    Id = call.Id,
                    Kind = outcome.Kind,
                    Status = outcome.Status,
                    LatencyMs = outcome.LatencyMs,
                    Hits = outcome.Hits,
                };

                collectedToolCalls.Add(new ToolCall
                {
                    Id = Guid.NewGuid().ToString("D"),
                    MessageId = turn.AssistantMessageId,
                    Kind = outcome.Kind,
                    Status = outcome.Status,
                    RequestJson = call.ArgumentsJson,
                    ResponseJson = outcome.ResponseJson,
                    LatencyMs = outcome.LatencyMs,
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                collectedCitations.AddRange(outcome.Citations);

                messages.Add(new ChatModelMessage
                {
                    Role = "tool",
                    Content = outcome.ResponseJson ?? string.Empty,
                    ToolCallId = call.Id,
                });
            }
        }

        if (faulted)
        {
            // The turn failed mid-stream: surface the error, persist nothing more.
            yield return terminalError!;
            yield break;
        }

        // Re-number citations into a single stable sequence over the whole turn and
        // bind them to the assistant message, then emit them after the text.
        var citations = RenumberCitations(collectedCitations, turn.AssistantMessageId);
        foreach (var citation in citations)
        {
            yield return new CitationEvent
            {
                Ordinal = citation.Ordinal,
                Label = citation.Label,
                DocId = citation.DocId,
            };
        }

        // Persist the assistant message (+ token count), its tool_calls, and citations.
        // The repo is re-opened per-use here — no handle was held across StartTurnAsync.
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

        foreach (var toolCall in collectedToolCalls)
        {
            await repo.InsertToolCallAsync(toolCall, cancellationToken).ConfigureAwait(false);
        }

        if (citations.Count > 0)
        {
            await repo.InsertCitationsAsync(citations, cancellationToken).ConfigureAwait(false);
        }

        yield return new MessageEndEvent { TokenCount = tokenCount };
    }

    /// <summary>
    /// Drive one model streaming call to completion, buffering its text deltas and
    /// tool calls. A model exception is captured as an <see cref="ErrorEvent"/>
    /// rather than thrown, so the caller can yield it without a yield-in-try.
    /// </summary>
    private async Task<ModelStep> DrainModelCallAsync(
        ChatCompletionRequest completion,
        CancellationToken cancellationToken)
    {
        var deltas = new List<string>();
        var toolCalls = new List<ChatModelToolCall>();
        int? tokenCount = null;
        ErrorEvent? error = null;

        try
        {
            await foreach (var chunk in _model.StreamAsync(completion, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    deltas.Add(chunk.TextDelta);
                }

                if (chunk.ToolCall is not null)
                {
                    toolCalls.Add(chunk.ToolCall);
                }

                if (chunk.TokenCount is not null)
                {
                    tokenCount = chunk.TokenCount;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's intent — propagate, don't mask as an error event.
            throw;
        }
        catch (Exception ex)
        {
            error = new ErrorEvent { Message = ex.Message };
        }

        return new ModelStep
        {
            Deltas = deltas,
            ToolCalls = toolCalls,
            TokenCount = tokenCount,
            Error = error,
        };
    }

    /// <summary>
    /// Execute one tool call against the active project. Re-checks the entitlement
    /// defensively (<see cref="IUserContext.CanUseTool"/>) — the claim is the
    /// ceiling at execution time too, never only at advertise time.
    /// </summary>
    private async Task<ToolOutcome> ExecuteToolAsync(
        string pid,
        ChatModelToolCall call,
        CancellationToken cancellationToken)
    {
        var tool = ResolveTool(call.Name);
        if (tool is null)
        {
            return ToolOutcome.Failure("unknown", $"no tool named '{call.Name}'");
        }

        // Defence-in-depth: never run a tool the user isn't entitled to, even if it
        // somehow reached the model (e.g. a poisoned history). The advertise-time
        // filter in StartTurnAsync is the primary gate; this is the second.
        if (!_user.CanUseTool(tool.Id))
        {
            return ToolOutcome.Failure(tool.Id, $"tool '{tool.Id}' is not permitted");
        }

        var invocation = new ToolInvocation { Pid = pid, ArgumentsJson = call.ArgumentsJson };
        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ToolOutcome.Failure(tool.Id, ex.Message, stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        return ToolOutcome.From(tool.Id, result, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Resolve the offered tool set: requested ∩ conversation-enabled ∩ entitlement
    /// ∩ registry, in registry order. The entitlement (<see cref="IUserContext"/>)
    /// is the hard ceiling — a tool not granted is never offered, even if requested
    /// and conversation-enabled (auth.md § the claim is the ceiling).
    /// </summary>
    private IReadOnlyList<ITool> ResolveOfferedTools(SendMessageRequest request, Conversation? conversation)
    {
        // Requested set: the message body's toggles, else the conversation's toggles.
        // An absent toggle map means "no tools requested this turn".
        var requested = request.Tools?.EnabledIds
                        ?? conversation?.Tools.EnabledIds
                        ?? new HashSet<string>(StringComparer.Ordinal);

        // Conversation-enabled set (a per-conversation preference). When the request
        // carries its own toggles we still gate against the conversation's enabled
        // set so flipping a conversation toggle off can't be re-enabled per-request.
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

    private ITool? ResolveTool(string functionName) =>
        _tools.FirstOrDefault(t => string.Equals(t.Name, functionName, StringComparison.Ordinal));

    private string ResolveKind(string functionName) =>
        ResolveTool(functionName)?.Id ?? functionName;

    private static ChatToolSpec ToSpec(ITool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        ParametersSchema = tool.ParametersSchema,
    };

    private static IReadOnlyDictionary<string, object?>? ParseArgs(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Collapse per-tool citation lists into one stable [1..n] sequence and bind
    /// each to the assistant message id.
    /// </summary>
    private static IReadOnlyList<Citation> RenumberCitations(
        IReadOnlyList<Citation> citations,
        string assistantMessageId)
    {
        var result = new List<Citation>(citations.Count);
        for (var i = 0; i < citations.Count; i++)
        {
            result.Add(citations[i] with
            {
                MessageId = assistantMessageId,
                Ordinal = i + 1,
            });
        }

        return result;
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
