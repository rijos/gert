using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Rag;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Testing.Fakes;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// Unit tests for the three U7c tools — each driven through its <see cref="ITool"/>
/// surface (parse args → call the port → shape the <see cref="ToolResult"/>),
/// using the shared fakes (<see cref="FakeEmbeddings"/>, <see cref="FakeWebSearch"/>,
/// <see cref="StubSandbox"/>) and an NSubstitute <see cref="IRagRepository"/>.
/// </summary>
public sealed class ToolsTests
{
    private static readonly TestUserContext User = new();

    // ---- RagTool -----------------------------------------------------------

    [Fact]
    public async Task RagTool_embeds_query_then_hybrid_searches_and_emits_document_citations()
    {
        var ragRepo = Substitute.For<IRagRepository>();
        var provider = Substitute.For<IDatabaseProvider>();
        provider
            .OpenRagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ragRepo);

        var hits = new[]
        {
            Hit("doc-1", "qdrant-benchmarks.pdf", "p.4", "sqlite-vec wins at this scale", 0.89),
            Hit("doc-2", "notes.md", null, "single-file stack", 0.77),
        };
        ragRepo
            .HybridSearchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(hits);

        var tool = new RagTool(provider, new FakeEmbeddings(), User);
        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"query\":\"qdrant\",\"k\":5}",
        });

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("qdrant-benchmarks.pdf");

        // The rag.db is opened for the user-context identity + the invocation pid.
        await provider.Received(1).OpenRagAsync(User.Iss, User.Sub, "default", Arg.Any<CancellationToken>());

        // The query was embedded and passed to the hybrid search (k clamped within range).
        await ragRepo.Received(1).HybridSearchAsync(
            "qdrant",
            Arg.Is<IReadOnlyList<float>>(v => v.Count == FakeEmbeddings.Dimensions),
            5,
            Arg.Any<CancellationToken>());

        // Two document citations, ordered, with labels/scores from the hits.
        result.Citations.Should().HaveCount(2);
        result.Citations[0].SourceType.Should().Be(CitationSourceType.Document);
        result.Citations[0].Label.Should().Be("qdrant-benchmarks.pdf · p.4");
        result.Citations[0].DocId.Should().Be("doc-1");
        result.Citations[0].Score.Should().Be(0.89);
        result.Citations[1].Label.Should().Be("notes.md");
    }

    [Fact]
    public async Task RagTool_rejects_missing_query()
    {
        var provider = Substitute.For<IDatabaseProvider>();
        var tool = new RagTool(provider, new FakeEmbeddings(), User);

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{}" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("query");

        // Fail-closed before any rag.db open.
        await provider.DidNotReceive().OpenRagAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- WebSearchTool -----------------------------------------------------

    [Fact]
    public async Task WebSearchTool_returns_web_citations_from_the_search_port()
    {
        var tool = new WebSearchTool(new FakeWebSearch());

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"query\":\"sqlite-vec benchmarks\"}",
        });

        result.Success.Should().BeTrue();
        result.Citations.Should().ContainSingle();
        var citation = result.Citations[0];
        citation.SourceType.Should().Be(CitationSourceType.Web);
        citation.Label.Should().Be("sqlite-vec benchmarks");
        citation.Locator.Should().Be("https://example.test/sqlite-vec-bench");
        result.ResultJson.Should().Contain("https://example.test/sqlite-vec-bench");
    }

    [Fact]
    public async Task WebSearchTool_rejects_missing_query()
    {
        var tool = new WebSearchTool(new FakeWebSearch());

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{}" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("query");
    }

    // ---- SandboxTool -------------------------------------------------------

    [Fact]
    public async Task SandboxTool_success_returns_stdout_and_exit_code()
    {
        var tool = new SandboxTool(StubSandbox.WithStdout("4\n"));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"print(2 + 2)\"}",
        });

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("4");
        result.ResultJson.Should().Contain("\"exit_code\":0");
        result.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task SandboxTool_nonzero_exit_is_a_graceful_failure()
    {
        var tool = new SandboxTool(StubSandbox.ThatFails("Traceback: boom", exitCode: 1));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"raise Exception()\"}",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("boom");
        // The stderr/exit still rides back to the model in the JSON payload.
        result.ResultJson.Should().Contain("\"exit_code\":1");
    }

    [Fact]
    public async Task SandboxTool_hard_throw_is_caught_not_propagated()
    {
        var tool = new SandboxTool(StubSandbox.ThatThrows(new InvalidOperationException("runsc spawn failed")));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"print(1)\"}",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("runsc spawn failed");
    }

    [Fact]
    public async Task SandboxTool_timeout_is_a_graceful_failure()
    {
        var tool = new SandboxTool(StubSandbox.ThatTimesOut());

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"while True: pass\"}",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out");
        result.ResultJson.Should().Contain("\"timed_out\":true");
    }

    [Fact]
    public async Task SandboxTool_success_surfaces_stdout_for_the_card()
    {
        var tool = new SandboxTool(StubSandbox.WithStdout("4\n"));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"print(2 + 2)\"}",
        });

        // The display seam: the card's pre block renders Stdout verbatim.
        result.Stdout.Should().Be("4\n");
    }

    // ---- RagTool: memory hits ----------------------------------------------

    [Fact]
    public async Task RagTool_decodes_a_memory_hits_title_for_the_label_and_payload()
    {
        var ragRepo = Substitute.For<IRagRepository>();
        var provider = Substitute.For<IDatabaseProvider>();
        provider
            .OpenRagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ragRepo);

        // A memory entry's documents.filename is the base64-encoded title
        // (MemoryService.EncodeTitle) — the tool must surface the decoded title.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Favorite database"));
        var memoryHit = Hit("mem-1", encoded, null, "My favorite database is sqlite-vec.", 0.92);
        memoryHit = memoryHit with { Document = memoryHit.Document with { Kind = DocumentKind.Memory } };
        ragRepo
            .HybridSearchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { memoryHit });

        var tool = new RagTool(provider, new FakeEmbeddings(), User);
        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"query\":\"favorite database\"}",
        });

        result.Success.Should().BeTrue();
        result.Citations.Should().ContainSingle();
        result.Citations[0].Label.Should().Be("Favorite database");
        result.ResultJson.Should().Contain("Favorite database")
            .And.Contain("\"kind\":\"memory\"")
            .And.NotContain(encoded);
    }

    // ---- TodoTool ----------------------------------------------------------

    [Fact]
    public async Task TodoTool_accepts_the_full_list_and_surfaces_it_for_the_card()
    {
        var tool = new TodoTool();

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson =
                "{\"todos\":[" +
                "{\"text\":\"Order the new SSD\",\"status\":\"done\"}," +
                "{\"text\":\"Migrate rag.db\",\"status\":\"active\"}," +
                "{\"text\":\"Re-embed the corpus\",\"status\":\"pending\"}]}",
        });

        result.Success.Should().BeTrue();
        result.Citations.Should().BeEmpty();

        // The display seam the todo card renders…
        result.Todos.Should().HaveCount(3);
        result.Todos![0].Should().Be(new TodoItem { Text = "Order the new SSD", Status = TodoStatus.Done });
        result.Todos[1].Status.Should().Be(TodoStatus.Active);
        result.Todos[2].Status.Should().Be(TodoStatus.Pending);

        // …and the model-facing echo (snake_case statuses) for the follow-up call.
        result.ResultJson.Should().Contain("\"status\":\"active\"").And.Contain("Migrate rag.db");
    }

    [Fact]
    public async Task TodoTool_rejects_a_missing_list_an_empty_text_and_a_bad_status()
    {
        var tool = new TodoTool();

        var missing = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{}" });
        missing.Success.Should().BeFalse();
        missing.Error.Should().Contain("todos");

        var blankText = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"todos\":[{\"text\":\"  \",\"status\":\"pending\"}]}",
        });
        blankText.Success.Should().BeFalse();
        blankText.Error.Should().Contain("text");

        var badStatus = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"todos\":[{\"text\":\"x\",\"status\":\"someday\"}]}",
        });
        badStatus.Success.Should().BeFalse();
        badStatus.Error.Should().Contain("status");
    }

    [Fact]
    public async Task TodoTool_malformed_json_is_a_graceful_failure()
    {
        var tool = new TodoTool();

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{not json" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid arguments");
    }

    // ---- ClockTool ---------------------------------------------------------

    /// <summary>The pinned instant the clock tests read through TimeProvider.</summary>
    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    [Fact]
    public async Task ClockTool_returns_the_pinned_instant_in_utc()
    {
        var instant = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
        var tool = new ClockTool(new FixedTime(instant));

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{}" });

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("2026-06-05T12:30:00")
            .And.Contain("\"timezone\":\"UTC\"")
            .And.Contain($"\"unix\":{instant.ToUnixTimeSeconds()}")
            .And.Contain("Friday");
        result.Stdout.Should().Be("2026-06-05 12:30:00 (UTC, Friday)");
        result.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task ClockTool_converts_to_a_requested_iana_timezone()
    {
        var instant = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
        var tool = new ClockTool(new FixedTime(instant));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"timezone\":\"Europe/Amsterdam\"}",
        });

        result.Success.Should().BeTrue();
        // CEST in June: UTC+2.
        result.ResultJson.Should().Contain("2026-06-05T14:30:00")
            .And.Contain("\"timezone\":\"Europe/Amsterdam\"");
        result.Stdout.Should().Contain("14:30:00 (Europe/Amsterdam");
    }

    [Fact]
    public async Task ClockTool_unknown_timezone_is_a_graceful_failure()
    {
        var tool = new ClockTool(new FixedTime(DateTimeOffset.UnixEpoch));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"timezone\":\"Mars/Olympus_Mons\"}",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Mars/Olympus_Mons");
    }

    // ---- helpers -----------------------------------------------------------

    private static RetrievedChunk Hit(
        string docId, string filename, string? page, string content, double score) => new()
    {
        Chunk = new Chunk
        {
            Id = 1,
            DocumentId = docId,
            Ordinal = 0,
            Content = content,
            Page = page,
        },
        Document = new Document
        {
            Id = docId,
            Filename = filename,
            Mime = "application/pdf",
            SizeBytes = 1024,
            Status = DocumentStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Score = score,
    };
}
