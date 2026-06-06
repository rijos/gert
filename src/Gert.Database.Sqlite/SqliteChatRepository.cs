using System.Globalization;
using System.Text.Json;
using Dapper;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Database;
using Microsoft.Data.Sqlite;

namespace Gert.Database.Sqlite;

/// <summary>
/// Dapper-backed <see cref="IChatRepository"/> over one project's <c>chat.db</c>
/// (storage-and-data.md § chat.db). Wraps a single open connection opened by the
/// provider (open-per-use); dispose closes it. The connection's path is the scope
/// — there is no project/user argument, so a query cannot reach another project's
/// rows.
///
/// <para>
/// Enums persist as the lowercase tokens the schema comments document
/// (<c>user|assistant|…</c>, <c>web_search</c>, …); timestamps persist as
/// round-trippable ISO-8601 (<c>o</c>) UTC text. <c>tools_json</c>/<c>params_json</c>
/// hold the serialised <see cref="ToolToggles"/>/<see cref="GenerationParams"/>.
/// </para>
/// </summary>
public sealed class SqliteChatRepository(SqliteConnection connection) : IChatRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static SqliteChatRepository()
    {
        // Bind snake_case columns to PascalCase DTO properties once, process-wide.
        // With property binding Dapper narrows SQLite's Int64 to the property type
        // (int?/int) automatically — so no `long` columns and no casts in the mappers.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private readonly SqliteConnection _connection = connection;

    // ---- conversations -----------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql =
            "SELECT id, title, model_id, tools_json, params_json, thinking, preserve_thinking, created_at, updated_at, archived " +
            "FROM conversations ORDER BY updated_at DESC, id ASC;";

        var rows = await _connection.QueryAsync<ConversationRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(MapConversation).ToList();
    }

    /// <inheritdoc />
    public async Task<Conversation?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        const string sql =
            "SELECT id, title, model_id, tools_json, params_json, thinking, preserve_thinking, created_at, updated_at, archived " +
            "FROM conversations WHERE id = @id;";

        var row = await _connection.QuerySingleOrDefaultAsync<ConversationRow>(
            new CommandDefinition(sql, new { id = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : MapConversation(row);
    }

    /// <inheritdoc />
    public async Task<ConversationThread?> GetThreadAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        var messages = await ListMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var artifacts = await ListArtifactsAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Tool calls + citations hang off the conversation's messages.
        const string toolSql =
            "SELECT tc.id, tc.message_id, tc.kind, tc.status, tc.request_json, tc.response_json, " +
            "       tc.latency_ms, tc.created_at " +
            "FROM tool_calls tc JOIN messages m ON m.id = tc.message_id " +
            "WHERE m.conversation_id = @cid ORDER BY tc.created_at ASC, tc.id ASC;";
        var toolRows = await _connection.QueryAsync<ToolCallRow>(
            new CommandDefinition(toolSql, new { cid = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        const string citeSql =
            "SELECT c.id, c.message_id, c.tool_call_id, c.ordinal, c.source_type, c.doc_id, c.label, c.locator, c.score " +
            "FROM citations c JOIN messages m ON m.id = c.message_id " +
            "WHERE m.conversation_id = @cid ORDER BY c.message_id ASC, c.ordinal ASC;";
        var citeRows = await _connection.QueryAsync<CitationRow>(
            new CommandDefinition(citeSql, new { cid = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new ConversationThread
        {
            Conversation = conversation,
            Messages = messages,
            ToolCalls = toolRows.Select(MapToolCall).ToList(),
            Citations = citeRows.Select(MapCitation).ToList(),
            Artifacts = artifacts,
        };
    }

    /// <inheritdoc />
    public async Task InsertConversationAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        const string sql =
            "INSERT INTO conversations (id, title, model_id, tools_json, params_json, thinking, preserve_thinking, created_at, updated_at, archived) " +
            "VALUES (@Id, @Title, @ModelId, @ToolsJson, @ParamsJson, @Thinking, @PreserveThinking, @CreatedAt, @UpdatedAt, @Archived);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            conversation.Id,
            conversation.Title,
            conversation.ModelId,
            ToolsJson = JsonSerializer.Serialize(conversation.Tools, JsonOptions),
            ParamsJson = JsonSerializer.Serialize(conversation.Params, JsonOptions),
            Thinking = conversation.Thinking switch { null => (int?)null, false => 0, true => 1 },
            PreserveThinking = conversation.PreserveThinking switch { null => (int?)null, false => 0, true => 1 },
            CreatedAt = FormatTime(conversation.CreatedAt),
            UpdatedAt = FormatTime(conversation.UpdatedAt),
            Archived = conversation.Archived ? 1 : 0,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateConversationAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        const string sql =
            "UPDATE conversations SET title = @Title, model_id = @ModelId, tools_json = @ToolsJson, " +
            "params_json = @ParamsJson, thinking = @Thinking, preserve_thinking = @PreserveThinking, " +
            "updated_at = @UpdatedAt, archived = @Archived WHERE id = @Id;";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            conversation.Id,
            conversation.Title,
            conversation.ModelId,
            ToolsJson = JsonSerializer.Serialize(conversation.Tools, JsonOptions),
            ParamsJson = JsonSerializer.Serialize(conversation.Params, JsonOptions),
            Thinking = conversation.Thinking switch { null => (int?)null, false => 0, true => 1 },
            PreserveThinking = conversation.PreserveThinking switch { null => (int?)null, false => 0, true => 1 },
            UpdatedAt = FormatTime(conversation.UpdatedAt),
            Archived = conversation.Archived ? 1 : 0,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        // ON DELETE CASCADE removes messages/tool_calls/citations/artifacts; the
        // provider enables PRAGMA foreign_keys=ON on every connection.
        const string sql = "DELETE FROM conversations WHERE id = @id;";
        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(sql, new { id = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected > 0;
    }

    // ---- messages ----------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<Message>> ListMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        // seq is the primary order; pre-v2 rows all have seq=0 and fall back to
        // created_at, so legacy threads keep their original order.
        const string sql =
            "SELECT id, conversation_id, role, content, model_id, token_count, reasoning, duration_ms, context_tokens, seq, status, created_at " +
            "FROM messages WHERE conversation_id = @cid ORDER BY seq ASC, created_at ASC, id ASC;";

        var rows = await _connection.QueryAsync<MessageRow>(
            new CommandDefinition(sql, new { cid = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(MapMessage).ToList();
    }

    /// <inheritdoc />
    public async Task InsertMessageAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        const string sql =
            "INSERT INTO messages (id, conversation_id, role, content, model_id, token_count, seq, status, created_at) " +
            "VALUES (@Id, @ConversationId, @Role, @Content, @ModelId, @TokenCount, @Seq, @Status, @CreatedAt);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            message.Id,
            message.ConversationId,
            Role = RoleToString(message.Role),
            message.Content,
            message.ModelId,
            message.TokenCount,
            message.Seq,
            Status = MessageStatusToString(message.Status),
            CreatedAt = FormatTime(message.CreatedAt),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateMessageStreamAsync(
        string messageId,
        string content,
        MessageStatus status,
        int? tokenCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(content);

        // COALESCE keeps an already-written token count when the caller has none
        // (intermediate flushes pass null; only the finish chunk carries usage).
        const string sql =
            "UPDATE messages SET content = @Content, status = @Status, " +
            "token_count = COALESCE(@TokenCount, token_count) WHERE id = @Id;";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = messageId,
            Content = content,
            Status = MessageStatusToString(status),
            TokenCount = tokenCount,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinalizeMessageAsync(
        string messageId,
        string content,
        MessageStatus status,
        int? tokenCount,
        string? reasoning,
        long? durationMs,
        int? contextTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(content);

        // COALESCE keeps earlier-written values when the caller has none (same
        // idiom as the streaming update's token_count).
        const string sql =
            "UPDATE messages SET content = @Content, status = @Status, " +
            "token_count = COALESCE(@TokenCount, token_count), " +
            "reasoning = COALESCE(@Reasoning, reasoning), " +
            "duration_ms = COALESCE(@DurationMs, duration_ms), " +
            "context_tokens = COALESCE(@ContextTokens, context_tokens) WHERE id = @Id;";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = messageId,
            Content = content,
            Status = MessageStatusToString(status),
            TokenCount = tokenCount,
            Reasoning = reasoning,
            DurationMs = durationMs,
            ContextTokens = contextTokens,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---- turn sequencing + replay log ---------------------------------------

    /// <inheritdoc />
    public async Task<long> AllocateSeqAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        // Atomic increment-and-return; the single source of seq. WAL + busy_timeout
        // serialize writers, and per-conversation single-writer is an invariant
        // (see IChatRepository.AllocateSeqAsync docs).
        const string sql =
            "UPDATE conversations SET next_seq = next_seq + 1 WHERE id = @id RETURNING next_seq - 1;";

        var seq = await _connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(sql, new { id = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return seq ?? throw new InvalidOperationException(
            $"Cannot allocate seq: conversation '{conversationId}' does not exist.");
    }

    /// <inheritdoc />
    public async Task AppendTurnEventAsync(
        TurnEventRecord turnEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turnEvent);

        const string sql =
            "INSERT INTO turn_events (conversation_id, seq, type, payload_json, created_at) " +
            "VALUES (@ConversationId, @Seq, @Type, @PayloadJson, @CreatedAt);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            turnEvent.ConversationId,
            turnEvent.Seq,
            turnEvent.Type,
            turnEvent.PayloadJson,
            CreatedAt = FormatTime(turnEvent.CreatedAt),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TurnEventRecord>> ReadTurnEventsAsync(
        string conversationId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        const string sql =
            "SELECT conversation_id, seq, type, payload_json, created_at " +
            "FROM turn_events WHERE conversation_id = @cid AND seq > @after " +
            "ORDER BY seq ASC LIMIT @limit;";

        var rows = await _connection.QueryAsync<TurnEventRow>(
            new CommandDefinition(
                sql,
                new { cid = conversationId, after = afterSeq, limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(MapTurnEvent).ToList();
    }

    // ---- tool calls --------------------------------------------------------

    /// <inheritdoc />
    public async Task InsertToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        const string sql =
            "INSERT INTO tool_calls (id, message_id, kind, status, request_json, response_json, latency_ms, created_at) " +
            "VALUES (@Id, @MessageId, @Kind, @Status, @RequestJson, @ResponseJson, @LatencyMs, @CreatedAt);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            toolCall.Id,
            toolCall.MessageId,
            toolCall.Kind,
            Status = ToolStatusToString(toolCall.Status),
            toolCall.RequestJson,
            toolCall.ResponseJson,
            toolCall.LatencyMs,
            CreatedAt = FormatTime(toolCall.CreatedAt),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ToolCall?> GetLatestToolCallAsync(
        string conversationId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            "SELECT tc.id, tc.message_id, tc.kind, tc.status, tc.request_json, tc.response_json, " +
            "       tc.latency_ms, tc.created_at " +
            "FROM tool_calls tc JOIN messages m ON m.id = tc.message_id " +
            "WHERE m.conversation_id = @cid AND tc.kind = @kind AND tc.status = 'done' " +
            "ORDER BY tc.created_at DESC, tc.id DESC LIMIT 1;";

        var row = await _connection.QuerySingleOrDefaultAsync<ToolCallRow>(
            new CommandDefinition(sql, new { cid = conversationId, kind }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : MapToolCall(row);
    }

    // ---- citations ---------------------------------------------------------

    /// <inheritdoc />
    public async Task InsertCitationsAsync(
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(citations);
        if (citations.Count == 0)
        {
            return;
        }

        const string sql =
            "INSERT INTO citations (id, message_id, tool_call_id, ordinal, source_type, doc_id, label, locator, score) " +
            "VALUES (@Id, @MessageId, @ToolCallId, @Ordinal, @SourceType, @DocId, @Label, @Locator, @Score);";

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var citation in citations)
        {
            await _connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                citation.Id,
                citation.MessageId,
                citation.ToolCallId,
                citation.Ordinal,
                SourceType = CitationSourceToString(citation.SourceType),
                citation.DocId,
                citation.Label,
                citation.Locator,
                citation.Score,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- artifacts ---------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<Artifact>> ListArtifactsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        const string sql =
            "SELECT id, conversation_id, message_id, kind, name, language, content, version, created_at " +
            "FROM artifacts WHERE conversation_id = @cid ORDER BY created_at ASC, id ASC;";

        var rows = await _connection.QueryAsync<ArtifactRow>(
            new CommandDefinition(sql, new { cid = conversationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(MapArtifact).ToList();
    }

    /// <inheritdoc />
    public async Task<Artifact?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactId);

        const string sql =
            "SELECT id, conversation_id, message_id, kind, name, language, content, version, created_at " +
            "FROM artifacts WHERE id = @id;";

        var row = await _connection.QuerySingleOrDefaultAsync<ArtifactRow>(
            new CommandDefinition(sql, new { id = artifactId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : MapArtifact(row);
    }

    /// <inheritdoc />
    public async Task InsertArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        const string sql =
            "INSERT INTO artifacts (id, conversation_id, message_id, kind, name, language, content, version, created_at) " +
            "VALUES (@Id, @ConversationId, @MessageId, @Kind, @Name, @Language, @Content, @Version, @CreatedAt);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            artifact.Id,
            artifact.ConversationId,
            artifact.MessageId,
            Kind = ArtifactKindToString(artifact.Kind),
            artifact.Name,
            artifact.Language,
            artifact.Content,
            artifact.Version,
            CreatedAt = FormatTime(artifact.CreatedAt),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ---- mapping -----------------------------------------------------------

    private static Conversation MapConversation(ConversationRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        ModelId = row.ModelId,
        Tools = Deserialize<ToolToggles>(row.ToolsJson) ?? new ToolToggles(),
        Params = Deserialize<GenerationParams>(row.ParamsJson) ?? new GenerationParams(),
        Thinking = row.Thinking switch { null => null, 0 => false, _ => true },
        PreserveThinking = row.PreserveThinking switch { null => null, 0 => false, _ => true },
        CreatedAt = ParseTime(row.CreatedAt),
        UpdatedAt = ParseTime(row.UpdatedAt),
        Archived = row.Archived != 0,
    };

    private static Message MapMessage(MessageRow row) => new()
    {
        Id = row.Id,
        ConversationId = row.ConversationId,
        Role = RoleFromString(row.Role),
        Content = row.Content,
        ModelId = row.ModelId,
        TokenCount = row.TokenCount,
        Reasoning = row.Reasoning,
        DurationMs = row.DurationMs,
        ContextTokens = row.ContextTokens,
        Seq = row.Seq,
        Status = MessageStatusFromString(row.Status),
        CreatedAt = ParseTime(row.CreatedAt),
    };

    private static TurnEventRecord MapTurnEvent(TurnEventRow row) => new()
    {
        ConversationId = row.ConversationId,
        Seq = row.Seq,
        Type = row.Type,
        PayloadJson = row.PayloadJson,
        CreatedAt = ParseTime(row.CreatedAt),
    };

    private static ToolCall MapToolCall(ToolCallRow row) => new()
    {
        Id = row.Id,
        MessageId = row.MessageId,
        Kind = row.Kind,
        Status = ToolStatusFromString(row.Status),
        RequestJson = row.RequestJson,
        ResponseJson = row.ResponseJson,
        LatencyMs = row.LatencyMs,
        CreatedAt = ParseTime(row.CreatedAt),
    };

    private static Citation MapCitation(CitationRow row) => new()
    {
        Id = row.Id,
        MessageId = row.MessageId,
        ToolCallId = row.ToolCallId,
        Ordinal = row.Ordinal,
        SourceType = CitationSourceFromString(row.SourceType),
        DocId = row.DocId,
        Label = row.Label,
        Locator = row.Locator,
        Score = row.Score,
    };

    private static Artifact MapArtifact(ArtifactRow row) => new()
    {
        Id = row.Id,
        ConversationId = row.ConversationId,
        MessageId = row.MessageId,
        Kind = ArtifactKindFromString(row.Kind),
        Name = row.Name,
        Language = row.Language,
        Content = row.Content,
        Version = row.Version,
        CreatedAt = ParseTime(row.CreatedAt),
    };

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Enum <-> on-disk token mappings (match the schema comments exactly).
    private static string RoleToString(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System => "system",
        MessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static MessageRole RoleFromString(string value) => value switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "system" => MessageRole.System,
        "tool" => MessageRole.Tool,
        _ => throw new InvalidOperationException($"Unknown message role '{value}'."),
    };

    private static string ToolStatusToString(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Running => "running",
        ToolCallStatus.Done => "done",
        ToolCallStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static ToolCallStatus ToolStatusFromString(string value) => value switch
    {
        "running" => ToolCallStatus.Running,
        "done" => ToolCallStatus.Done,
        "error" => ToolCallStatus.Error,
        _ => throw new InvalidOperationException($"Unknown tool-call status '{value}'."),
    };

    private static string MessageStatusToString(MessageStatus status) => status switch
    {
        MessageStatus.Streaming => "streaming",
        MessageStatus.Complete => "complete",
        MessageStatus.Error => "error",
        MessageStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static MessageStatus MessageStatusFromString(string value) => value switch
    {
        "streaming" => MessageStatus.Streaming,
        "complete" => MessageStatus.Complete,
        "error" => MessageStatus.Error,
        "cancelled" => MessageStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown message status '{value}'."),
    };

    private static string CitationSourceToString(CitationSourceType type) => type switch
    {
        CitationSourceType.Document => "document",
        CitationSourceType.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static CitationSourceType CitationSourceFromString(string value) => value switch
    {
        "document" => CitationSourceType.Document,
        "web" => CitationSourceType.Web,
        _ => throw new InvalidOperationException($"Unknown citation source '{value}'."),
    };

    private static string ArtifactKindToString(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Md => "md",
        ArtifactKind.Html => "html",
        ArtifactKind.Svg => "svg",
        ArtifactKind.Py => "py",
        ArtifactKind.Cs => "cs",
        ArtifactKind.Cpp => "cpp",
        ArtifactKind.Js => "js",
        ArtifactKind.Rs => "rs",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static ArtifactKind ArtifactKindFromString(string value) => value switch
    {
        "md" => ArtifactKind.Md,
        "html" => ArtifactKind.Html,
        "svg" => ArtifactKind.Svg,
        "py" => ArtifactKind.Py,
        "cs" => ArtifactKind.Cs,
        "cpp" => ArtifactKind.Cpp,
        "js" => ArtifactKind.Js,
        "rs" => ArtifactKind.Rs,
        _ => throw new InvalidOperationException($"Unknown artifact kind '{value}'."),
    };

    // ---- row DTOs ----------------------------------------------------------
    // PascalCase properties; Dapper binds snake_case columns via
    // MatchNamesWithUnderscores (set in the static ctor) and narrows SQLite's
    // Int64 to each property's declared type — no `long` widening, no casts.

    private sealed record ConversationRow
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string ModelId { get; init; }
        public required string ToolsJson { get; init; }
        public required string ParamsJson { get; init; }
        public long? Thinking { get; init; }
        public long? PreserveThinking { get; init; }
        public required string CreatedAt { get; init; }
        public required string UpdatedAt { get; init; }
        public int Archived { get; init; }
    }

    private sealed record MessageRow
    {
        public required string Id { get; init; }
        public required string ConversationId { get; init; }
        public required string Role { get; init; }
        public required string Content { get; init; }
        public string? ModelId { get; init; }
        public int? TokenCount { get; init; }
        public string? Reasoning { get; init; }
        public long? DurationMs { get; init; }
        public int? ContextTokens { get; init; }
        public long Seq { get; init; }
        public required string Status { get; init; }
        public required string CreatedAt { get; init; }
    }

    private sealed record TurnEventRow
    {
        public required string ConversationId { get; init; }
        public long Seq { get; init; }
        public required string Type { get; init; }
        public required string PayloadJson { get; init; }
        public required string CreatedAt { get; init; }
    }

    private sealed record ToolCallRow
    {
        public required string Id { get; init; }
        public required string MessageId { get; init; }
        public required string Kind { get; init; }
        public required string Status { get; init; }
        public string? RequestJson { get; init; }
        public string? ResponseJson { get; init; }
        public long? LatencyMs { get; init; }
        public required string CreatedAt { get; init; }
    }

    private sealed record CitationRow
    {
        public required string Id { get; init; }
        public required string MessageId { get; init; }
        public string? ToolCallId { get; init; }
        public int Ordinal { get; init; }
        public required string SourceType { get; init; }
        public string? DocId { get; init; }
        public required string Label { get; init; }
        public string? Locator { get; init; }
        public double? Score { get; init; }
    }

    private sealed record ArtifactRow
    {
        public required string Id { get; init; }
        public required string ConversationId { get; init; }
        public string? MessageId { get; init; }
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public string? Language { get; init; }
        public required string Content { get; init; }
        public int Version { get; init; }
        public required string CreatedAt { get; init; }
    }
}
