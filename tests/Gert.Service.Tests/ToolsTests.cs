using FluentAssertions;
using Gert.Model;
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
