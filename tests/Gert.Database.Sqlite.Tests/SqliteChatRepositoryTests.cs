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
    public async Task Conversation_round_trips_with_tools_and_params()
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
            Params = new GenerationParams { Temperature = 0.7, MaxTokens = 512 },
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
        loaded.Params.Temperature.Should().Be(0.7);
        loaded.Params.MaxTokens.Should().Be(512);
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
                Label = "qdrant-benchmarks.pdf · p.4",
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
        // Both kind mappers throw on an unmapped value — enumerating the enum
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
            });
        }

        var artifacts = await repo.ListArtifactsAsync(conversation.Id);

        artifacts.Select(a => a.Kind).Should().BeEquivalentTo(Enum.GetValues<ArtifactKind>());
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
        var message = NewMessage(conversation.Id, MessageRole.Assistant, "", DateTimeOffset.UtcNow) with
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

        // Finalize with a token count…
        await repo.UpdateMessageStreamAsync(message.Id, "full answer", MessageStatus.Complete, 42);
        // …then a hypothetical later null-token update must NOT erase it (COALESCE).
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

        // Same created_at, distinct seq — seq decides; insertion order is shuffled.
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
                Label = "spec.pdf · p.2",
            },
        });

        var thread = await repo.GetThreadAsync(conversation.Id);

        thread!.Citations.Should().ContainSingle()
            .Which.ToolCallId.Should().Be(toolCallId);
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
    };
}
