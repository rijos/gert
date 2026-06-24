using FluentAssertions;
using Gert.Database.Sqlite;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>Round-trips against a real provisioned chat.db.</summary>
public class SqliteChatRepositoryTests
{
    private const string Sub = "alice-sub";

    [Fact]
    public async Task Conversation_round_trips_with_tools()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation() with
        {
            Tools = new ToolToggles(new Dictionary<string, bool>
            {
                ["rag"] = true,
                ["search"] = true,
                ["sandbox"] = false,
            }),
        };

        await using (var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default"))
        {
            await repo.InsertConversationAsync(conversation);
        }

        await using var readRepo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        var loaded = await readRepo.GetConversationAsync(conversation.Id);

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be(conversation.Title);
        loaded.ModelId.Should().Be(conversation.ModelId);
        loaded.Tools.Should().Be(conversation.Tools);
        loaded.CreatedAt.Should().BeCloseTo(conversation.CreatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Messages_round_trip_in_created_at_order()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var baseTime = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        // Insert out of chronological order to prove ORDER BY created_at.
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.Assistant, "second", baseTime.AddSeconds(2)));
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.User, "first", baseTime.AddSeconds(1)));
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.User, "third", baseTime.AddSeconds(3)));

        var messages = await repo.ListMessagesAsync(conversation.Id);

        messages.Select(m => m.Content).Should().ContainInOrder("first", "second", "third");
        messages[0].Role.Should().Be(MessageRole.User);
        messages[1].Role.Should().Be(MessageRole.Assistant);
    }

    [Fact]
    public async Task Message_attachments_round_trip_and_absent_stays_null()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var withImage = NewMessage(conversation.Id, MessageRole.User, "what is this?", now) with
        {
            Attachments =
            [
                new MessageAttachment { MimeType = "image/png", Data = "aGVsbG8=" },
                new MessageAttachment { MimeType = "image/jpeg", Data = "d29ybGQ=" },
            ],
        };
        var withoutImage = NewMessage(conversation.Id, MessageRole.Assistant, "a cat", now.AddSeconds(1));

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(withImage);
        await repo.InsertMessageAsync(withoutImage);

        var messages = await repo.ListMessagesAsync(conversation.Id);

        var user = messages.Single(m => m.Role == MessageRole.User);
        user.Attachments.Should().HaveCount(2);
        user.Attachments![0].Should().Be(new MessageAttachment { MimeType = "image/png", Data = "aGVsbG8=" });
        user.Attachments[1].MimeType.Should().Be("image/jpeg");
        messages.Single(m => m.Role == MessageRole.Assistant).Attachments.Should().BeNull();
    }

    [Fact]
    public async Task Thread_loads_messages_tool_calls_citations_and_artifacts()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(conversation.Id, MessageRole.Assistant, "answer [1]", now);

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(message);
        await repo.InsertToolCallAsync(new ToolCall
        {
            Id = Guid.NewGuid().ToString("D"),
            MessageId = message.Id,
            Kind = "web_search",
            Status = ToolCallStatus.Done,
            RequestJson = "{\"q\":\"x\"}",
            ResponseJson = "{\"hits\":1}",
            LatencyMs = 142,
            CreatedAt = now,
        });
        await repo.InsertCitationsAsync(new[]
        {
            new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = message.Id,
                Ordinal = 1,
                SourceType = CitationSourceType.Document,
                DocId = "doc-1",
                Label = "qdrant-benchmarks.pdf - p.4",
                Locator = "p.4",
                Score = 0.89,
            },
        });
        await repo.InsertArtifactAsync(new Artifact
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            MessageId = message.Id,
            Kind = ArtifactKind.Md,
            Name = "decision.md",
            Content = "# Decision",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var thread = await repo.GetThreadAsync(conversation.Id);

        thread.Should().NotBeNull();
        thread!.Messages.Should().ContainSingle();
        thread.ToolCalls.Should().ContainSingle()
            .Which.Kind.Should().Be("web_search");
        thread.Citations.Should().ContainSingle()
            .Which.Score.Should().Be(0.89);
        thread.Artifacts.Should().ContainSingle()
            .Which.Name.Should().Be("decision.md");
    }

    [Fact]
    public async Task Thread_returns_messages_and_artifacts_in_chronological_order()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var baseTime = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        // Insert messages out of insertion order with varying created_at.
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.Assistant, "m-second", baseTime.AddSeconds(2)));
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.User, "m-first", baseTime.AddSeconds(1)));
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.User, "m-third", baseTime.AddSeconds(3)));

        // Insert artifacts out of insertion order with varying created_at.
        await repo.InsertArtifactAsync(NewArtifact(conversation.Id, "a-third", baseTime.AddSeconds(3)));
        await repo.InsertArtifactAsync(NewArtifact(conversation.Id, "a-first", baseTime.AddSeconds(1)));
        await repo.InsertArtifactAsync(NewArtifact(conversation.Id, "a-second", baseTime.AddSeconds(2)));

        var thread = await repo.GetThreadAsync(conversation.Id);

        thread.Should().NotBeNull();
        thread!.Messages.Select(m => m.Content).Should().Equal("m-first", "m-second", "m-third");
        thread.Artifacts.Select(a => a.Name).Should().Equal("a-first", "a-second", "a-third");
    }

    [Fact]
    public async Task Every_artifact_kind_round_trips_through_persistence()
    {
        // Both kind mappers throw on an unmapped value - enumerating the enum
        // here means a future ArtifactKind missing from either mapper fails in
        // tests instead of at runtime.
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await repo.InsertArtifactAsync(new Artifact
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = conversation.Id,
                MessageId = null,
                Kind = kind,
                Name = $"a.{kind}",
                Content = "body",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var artifacts = await repo.ListArtifactsAsync(conversation.Id);

        artifacts.Select(a => a.Kind).Should().BeEquivalentTo(Enum.GetValues<ArtifactKind>());
    }

    [Fact]
    public async Task Artifact_insert_update_and_delete_by_name_round_trip()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var created = DateTimeOffset.UtcNow;
        var updated = created.AddMinutes(5);

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        var artifact = new Artifact
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            MessageId = null,
            Kind = ArtifactKind.Md,
            Name = "notes.md",
            Content = "v1",
            Version = 1,
            CreatedAt = created,
            UpdatedAt = created,
        };
        await repo.InsertArtifactAsync(artifact);

        // updated_at round-trips; an overwrite bumps version + updated_at, keeps created_at.
        await repo.UpdateArtifactAsync(artifact with { Content = "v2", Version = 2, UpdatedAt = updated });

        var byName = await repo.GetArtifactByNameAsync(conversation.Id, "notes.md");
        byName.Should().NotBeNull();
        byName!.Content.Should().Be("v2");
        byName.Version.Should().Be(2);
        byName.CreatedAt.Should().BeCloseTo(created, TimeSpan.FromSeconds(1));
        byName.UpdatedAt.Should().BeCloseTo(updated, TimeSpan.FromSeconds(1));
        // chat_objects drops message_id/language: they are null off the persisted row.
        byName.MessageId.Should().BeNull();
        byName.Language.Should().BeNull();

        // Delete by name removes it (and is a no-op the second time).
        (await repo.DeleteArtifactByNameAsync(conversation.Id, "notes.md")).Should().BeTrue();
        (await repo.DeleteArtifactByNameAsync(conversation.Id, "notes.md")).Should().BeFalse();
        (await repo.GetArtifactByNameAsync(conversation.Id, "notes.md")).Should().BeNull();
    }

    [Fact]
    public async Task Two_artifacts_with_the_same_name_in_a_conversation_violate_the_unique_index()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertArtifactAsync(NewArtifact(conversation.Id, "dup.md", now));

        // UNIQUE(conversation_id, name): a second row with the same name is rejected.
        var second = () => repo.InsertArtifactAsync(NewArtifact(conversation.Id, "dup.md", now));
        await second.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Delete_conversation_cascades_to_children()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(conversation.Id, MessageRole.User, "hi", now);

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(message);
        await repo.InsertToolCallAsync(new ToolCall
        {
            Id = Guid.NewGuid().ToString("D"),
            MessageId = message.Id,
            Kind = "rag",
            Status = ToolCallStatus.Done,
            CreatedAt = now,
        });
        await repo.InsertArtifactAsync(new Artifact
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            MessageId = message.Id,
            Kind = ArtifactKind.Py,
            Name = "x.py",
            Content = "print(1)",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var deleted = await repo.DeleteConversationAsync(conversation.Id);

        deleted.Should().BeTrue();
        (await repo.GetConversationAsync(conversation.Id)).Should().BeNull();
        (await repo.ListMessagesAsync(conversation.Id)).Should().BeEmpty();
        (await repo.ListArtifactsAsync(conversation.Id)).Should().BeEmpty();

        // FK cascade also cleared the tool_calls row (joined via the message).
        await using var raw = new SqliteConnection($"Data Source={ProviderFixture.PathsFor(root).ChatDb(ProviderFixture.ExpectedIssuer, Sub, "default")}");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tool_calls;";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AllocateSeq_is_monotonic_from_one_and_throws_for_missing_conversation()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        var seqs = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            seqs.Add(await repo.AllocateSeqAsync(conversation.Id));
        }

        seqs.Should().Equal(1, 2, 3, 4, 5);

        var missing = () => repo.AllocateSeqAsync("nope");
        await missing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TurnEvents_append_and_read_by_cursor_in_seq_order()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        for (var i = 1; i <= 4; i++)
        {
            var seq = await repo.AllocateSeqAsync(conversation.Id);
            await repo.AppendTurnEventAsync(new TurnEventRecord
            {
                ConversationId = conversation.Id,
                Seq = seq,
                Type = "delta",
                PayloadJson = $"{{\"text\":\"chunk-{i}\"}}",
                CreatedAt = now,
            });
        }

        // Cursor semantics: seq > after, ascending, capped by limit.
        var page = await repo.ReadTurnEventsAsync(conversation.Id, afterSeq: 1, limit: 2);
        page.Select(e => e.Seq).Should().Equal(2, 3);
        page[0].PayloadJson.Should().Contain("chunk-2");

        var tail = await repo.ReadTurnEventsAsync(conversation.Id, afterSeq: 3, limit: 10);
        tail.Select(e => e.Seq).Should().Equal(4);

        (await repo.ReadTurnEventsAsync(conversation.Id, afterSeq: 4, limit: 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateMessageStream_flushes_content_and_keeps_token_count_when_null()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var message = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow) with
        {
            Seq = 1,
            Status = MessageStatus.Streaming,
        };

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(message);

        // Intermediate flush: still streaming, no token count yet.
        await repo.UpdateMessageStreamAsync(message.Id, "partial", MessageStatus.Streaming, null);
        var mid = (await repo.ListMessagesAsync(conversation.Id)).Single();
        mid.Content.Should().Be("partial");
        mid.Status.Should().Be(MessageStatus.Streaming);
        mid.Seq.Should().Be(1);

        // Finalize with a token count...
        await repo.UpdateMessageStreamAsync(message.Id, "full answer", MessageStatus.Complete, 42);
        // ...then a hypothetical later null-token update must NOT erase it (COALESCE).
        await repo.UpdateMessageStreamAsync(message.Id, "full answer", MessageStatus.Complete, null);

        var final = (await repo.ListMessagesAsync(conversation.Id)).Single();
        final.Content.Should().Be("full answer");
        final.Status.Should().Be(MessageStatus.Complete);
        final.TokenCount.Should().Be(42);
    }

    [Fact]
    public async Task Messages_order_by_seq_before_created_at()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        // Same created_at, distinct seq - seq decides; insertion order is shuffled.
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.Assistant, "second", now) with { Seq = 2 });
        await repo.InsertMessageAsync(NewMessage(conversation.Id, MessageRole.User, "first", now) with { Seq = 1 });

        var messages = await repo.ListMessagesAsync(conversation.Id);
        messages.Select(m => m.Content).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Citation_tool_call_id_round_trips_through_thread()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(conversation.Id, MessageRole.Assistant, "answer [1]", now);
        var toolCallId = Guid.NewGuid().ToString("D");

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(message);
        await repo.InsertToolCallAsync(new ToolCall
        {
            Id = toolCallId,
            MessageId = message.Id,
            Kind = "rag",
            Status = ToolCallStatus.Done,
            CreatedAt = now,
        });
        await repo.InsertCitationsAsync(new[]
        {
            new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = message.Id,
                ToolCallId = toolCallId,
                Ordinal = 1,
                SourceType = CitationSourceType.Document,
                DocId = "doc-1",
                Label = "spec.pdf - p.2",
            },
        });

        var thread = await repo.GetThreadAsync(conversation.Id);

        thread!.Citations.Should().ContainSingle()
            .Which.ToolCallId.Should().Be(toolCallId);
    }

    [Fact]
    public async Task Latest_tool_call_returns_newest_done_row_of_the_kind_scoped_to_the_conversation()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var other = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(conversation.Id, MessageRole.Assistant, "planned", now);
        var otherMessage = NewMessage(other.Id, MessageRole.Assistant, "elsewhere", now);

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertConversationAsync(other);
        await repo.InsertMessageAsync(message);
        await repo.InsertMessageAsync(otherMessage);

        ToolCall NewTodoCall(string messageId, ToolCallStatus status, string response, DateTimeOffset at, string kind = "todo") => new()
        {
            Id = Guid.NewGuid().ToString("D"),
            MessageId = messageId,
            Kind = kind,
            Status = status,
            RequestJson = "{}",
            ResponseJson = response,
            CreatedAt = at,
        };

        // The newest DONE todo row is the truth; an older snapshot, a newer
        // errored call, another kind, and another conversation never win.
        await repo.InsertToolCallAsync(NewTodoCall(message.Id, ToolCallStatus.Done, "{\"todos\":[\"old\"]}", now.AddSeconds(1)));
        await repo.InsertToolCallAsync(NewTodoCall(message.Id, ToolCallStatus.Done, "{\"todos\":[\"latest\"]}", now.AddSeconds(2)));
        await repo.InsertToolCallAsync(NewTodoCall(message.Id, ToolCallStatus.Error, "{\"todos\":[\"failed\"]}", now.AddSeconds(3)));
        await repo.InsertToolCallAsync(NewTodoCall(message.Id, ToolCallStatus.Done, "{\"now\":\"x\"}", now.AddSeconds(4), kind: "clock"));
        await repo.InsertToolCallAsync(NewTodoCall(otherMessage.Id, ToolCallStatus.Done, "{\"todos\":[\"other\"]}", now.AddSeconds(5)));

        var latest = await repo.GetLatestToolCallAsync(conversation.Id, "todo");

        latest.Should().NotBeNull();
        latest!.ResponseJson.Should().Be("{\"todos\":[\"latest\"]}");

        (await repo.GetLatestToolCallAsync(conversation.Id, "rag")).Should().BeNull();
    }

    // The streaming turn gate (ux_messages_streaming, decisions section 11).

    [Fact]
    public async Task Gate_rejects_a_second_streaming_insert_for_the_same_conversation()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var user1 = NewMessage(conversation.Id, MessageRole.User, "first question", now) with { Seq = 1 };
        var assistant1 = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with
        {
            Seq = 2,
            Status = MessageStatus.Streaming,
        };
        var user2 = NewMessage(conversation.Id, MessageRole.User, "racing question", now) with { Seq = 3 };
        var assistant2 = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with
        {
            Seq = 4,
            Status = MessageStatus.Streaming,
        };

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        (await repo.TryInsertTurnMessagesAsync(user1, assistant1)).Should().BeTrue();
        (await repo.TryInsertTurnMessagesAsync(user2, assistant2)).Should().BeFalse();

        // Atomicity: the losing pair persisted NEITHER row - not even the user one.
        var messages = await repo.ListMessagesAsync(conversation.Id);
        messages.Select(m => m.Id).Should().BeEquivalentTo([user1.Id, assistant1.Id]);

        // The index, not the helper, is the control: a direct second streaming
        // insert hits the engine-level constraint too.
        var direct = () => repo.InsertMessageAsync(
            NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with { Seq = 5, Status = MessageStatus.Streaming });
        await direct.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Gate_allows_streaming_rows_in_different_conversations()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var first = NewConversation();
        var second = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(first);
        await repo.InsertConversationAsync(second);

        var firstPlanned = await repo.TryInsertTurnMessagesAsync(
            NewMessage(first.Id, MessageRole.User, "q1", now) with { Seq = 1 },
            NewMessage(first.Id, MessageRole.Assistant, string.Empty, now) with { Seq = 2, Status = MessageStatus.Streaming });
        var secondPlanned = await repo.TryInsertTurnMessagesAsync(
            NewMessage(second.Id, MessageRole.User, "q2", now) with { Seq = 1 },
            NewMessage(second.Id, MessageRole.Assistant, string.Empty, now) with { Seq = 2, Status = MessageStatus.Streaming });

        firstPlanned.Should().BeTrue();
        secondPlanned.Should().BeTrue("the gate is per conversation, not per database");
    }

    [Fact]
    public async Task Finalizing_a_row_frees_the_gate()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);

        // Turn 1 holds the gate, completes normally -> the gate frees.
        var placeholder1 = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with
        {
            Seq = 2,
            Status = MessageStatus.Streaming,
        };
        (await repo.TryInsertTurnMessagesAsync(
            NewMessage(conversation.Id, MessageRole.User, "q1", now) with { Seq = 1 },
            placeholder1)).Should().BeTrue();
        await repo.FinalizeMessageAsync(placeholder1.Id, "answer", MessageStatus.Complete, 10, null, null, null);

        // Turn 2 holds the gate, dies, the planner write-back expires it -> frees again.
        var placeholder2 = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with
        {
            Seq = 4,
            Status = MessageStatus.Streaming,
        };
        (await repo.TryInsertTurnMessagesAsync(
            NewMessage(conversation.Id, MessageRole.User, "q2", now) with { Seq = 3 },
            placeholder2)).Should().BeTrue();
        (await repo.TryExpireStreamingMessageAsync(placeholder2.Id)).Should().BeTrue();

        // Turn 3: the conversation accepts a new streaming placeholder.
        (await repo.TryInsertTurnMessagesAsync(
            NewMessage(conversation.Id, MessageRole.User, "q3", now) with { Seq = 5 },
            NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, now) with { Seq = 6, Status = MessageStatus.Streaming }))
            .Should().BeTrue();
    }

    [Fact]
    public async Task TryExpireStreamingMessage_is_conditional_and_keeps_content()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);

        var conversation = NewConversation();
        var now = DateTimeOffset.UtcNow;
        var streaming = NewMessage(conversation.Id, MessageRole.Assistant, "partial text", now) with
        {
            Seq = 1,
            Status = MessageStatus.Streaming,
        };
        var complete = NewMessage(conversation.Id, MessageRole.Assistant, "done", now) with
        {
            Seq = 2,
            Status = MessageStatus.Complete,
        };

        await using var repo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, Sub, "default");
        await repo.InsertConversationAsync(conversation);
        await repo.InsertMessageAsync(streaming);
        await repo.InsertMessageAsync(complete);

        // True exactly once; the repeat and the already-final row are no-ops.
        (await repo.TryExpireStreamingMessageAsync(streaming.Id)).Should().BeTrue();
        (await repo.TryExpireStreamingMessageAsync(streaming.Id)).Should().BeFalse("the transition already happened");
        (await repo.TryExpireStreamingMessageAsync(complete.Id)).Should().BeFalse("a finalized row is never clobbered");

        var rows = await repo.ListMessagesAsync(conversation.Id);
        var expired = rows.Single(m => m.Id == streaming.Id);
        expired.Status.Should().Be(MessageStatus.Error);
        expired.Content.Should().Be("partial text", "the dead turn's partial content survives the write-back");
        rows.Single(m => m.Id == complete.Id).Status.Should().Be(MessageStatus.Complete);
    }

    private static Conversation NewConversation() => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Title = "Test chat",
        ModelId = "qwen3-27b-fp8-mtp",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Message NewMessage(string conversationId, MessageRole role, string content, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        ConversationId = conversationId,
        Role = role,
        Content = content,
        CreatedAt = at,
    };

    private static Artifact NewArtifact(string conversationId, string name, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        ConversationId = conversationId,
        MessageId = null,
        Kind = ArtifactKind.Md,
        Name = name,
        Content = "# " + name,
        CreatedAt = at,
        UpdatedAt = at,
    };
}
