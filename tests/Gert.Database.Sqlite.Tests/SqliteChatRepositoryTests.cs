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
            Tools = new ToolToggles { Rag = true, Search = true, Sandbox = false },
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
            Kind = ToolKind.WebSearch,
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
            .Which.Kind.Should().Be(ToolKind.WebSearch);
        thread.Citations.Should().ContainSingle()
            .Which.Score.Should().Be(0.89);
        thread.Artifacts.Should().ContainSingle()
            .Which.Name.Should().Be("decision.md");
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
            Kind = ToolKind.Rag,
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
}
