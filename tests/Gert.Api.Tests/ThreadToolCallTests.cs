using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Model;
using Gert.Model.Chat;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The thread GET's tool-card reconstruction (<see cref="ThreadToolCall"/>):
/// persisted <c>tool_calls</c> rows + their citations project back into the
/// SPA's card source fields, so reloading a conversation reproduces the cards
/// (incl. the todo checklist) the live tool_call/tool_result stream drew.
/// </summary>
public sealed class ThreadToolCallTests
{
    private static ToolCall Call(
        string kind,
        string? requestJson = null,
        string? responseJson = null,
        string id = "tc-1",
        string messageId = "m-1",
        ToolCallStatus status = ToolCallStatus.Done) => new()
    {
        Id = id,
        MessageId = messageId,
        Kind = kind,
        Status = status,
        RequestJson = requestJson,
        ResponseJson = responseJson,
        LatencyMs = 42,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Rag_call_carries_its_query_and_doc_hits_from_citations()
    {
        var citations = new[]
        {
            new Citation
            {
                Id = "c-1",
                MessageId = "m-1",
                ToolCallId = "tc-1",
                Ordinal = 1,
                SourceType = CitationSourceType.Document,
                Label = "qdrant-benchmarks.pdf",
                Locator = "p.4",
                Score = 0.91,
            },
        };

        var card = ThreadToolCall.From(Call("rag", requestJson: """{"query":"hnsw recall"}"""), citations);

        card.Kind.Should().Be("rag");
        card.Query.Should().Be("hnsw recall");
        card.LatencyMs.Should().Be(42);
        card.Hits.Should().ContainSingle(h => h.Doc == "qdrant-benchmarks.pdf" && h.Page == "p.4");
    }

    [Fact]
    public void Todo_call_rebuilds_the_checklist_from_response_json()
    {
        var card = ThreadToolCall.From(
            Call("todo", responseJson:
                """{"todos":[{"text":"write file_organizer.py","status":"done"},{"text":"write text_analyzer.py","status":"active"}],"reminder":"…"}"""),
            []);

        card.Todos.Should().HaveCount(2);
        card.Todos[0].Should().Be(new TodoItem { Text = "write file_organizer.py", Status = TodoStatus.Done });
        card.Todos[1].Status.Should().Be(TodoStatus.Active);
    }

    [Fact]
    public void Sandbox_call_carries_code_and_stdout()
    {
        var card = ThreadToolCall.From(
            Call(
                "sandbox",
                requestJson: """{"code":"print(1+1)"}""",
                responseJson: """{"exit_code":0,"stdout":"2\n","stderr":"","timed_out":false}"""),
            []);

        card.Code.Should().Be("print(1+1)");
        card.Stdout.Should().Be("2\n");
    }

    [Fact]
    public void Clock_call_reconstructs_the_human_reading()
    {
        var card = ThreadToolCall.From(
            Call("clock", responseJson:
                """{"utc":"2026-06-06T17:00:00.0000000+00:00","local":"2026-06-06T19:00:00.0000000+02:00","timezone":"Europe/Amsterdam","unix":1780765200,"day_of_week":"Saturday"}"""),
            []);

        card.Stdout.Should().Be("2026-06-06 19:00:00 (Europe/Amsterdam, Saturday)");
    }

    [Fact]
    public void Corrupt_payloads_degrade_to_an_empty_card_never_a_throw()
    {
        var card = ThreadToolCall.From(Call("rag", requestJson: "{not json", responseJson: "[]"), []);

        card.Query.Should().BeNull();
        card.Stdout.Should().BeNull();
        card.Todos.Should().BeEmpty();
        card.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Thread_response_binds_cards_to_their_message_in_call_order()
    {
        var thread = new ConversationThread
        {
            Conversation = new Conversation
            {
                Id = "conv-1",
                Title = "t",
                ModelId = "qwen",
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            },
            Messages =
            [
                new Message
                {
                    Id = "m-1",
                    ConversationId = "conv-1",
                    Role = MessageRole.Assistant,
                    Content = "done!",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                },
            ],
            ToolCalls =
            [
                Call("todo", id: "tc-2", responseJson: """{"todos":[{"text":"a","status":"done"}]}""") with
                {
                    CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
                },
                Call("rag", id: "tc-1", requestJson: """{"query":"q"}""") with
                {
                    CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
                },
                Call("rag", id: "tc-other", messageId: "m-other"),
            ],
        };

        var response = ThreadResponse.From(thread);

        var tools = response.Messages.Single().Tools;
        tools.Select(t => t.Id).Should().Equal("tc-1", "tc-2"); // CreatedAt order
        tools[1].Todos.Should().ContainSingle(t => t.Text == "a");
    }
}
