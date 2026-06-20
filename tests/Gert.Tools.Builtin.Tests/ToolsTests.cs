using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for the core tools -- each driven through its <see cref="ITool"/>
/// surface (parse args -> call the port -> shape the <see cref="ToolResult"/>),
/// using the shared fakes (<see cref="FakeWebSearch"/>, <see cref="StubPythonSandbox"/>)
/// and the host's scripted RAG resource (<see cref="FakeToolHost.ScriptedRagResource"/>).
/// </summary>
public sealed class ToolsTests
{
    [Fact]
    public async Task RagTool_searches_the_host_rag_resource_and_emits_document_citations()
    {
        var host = new FakeToolHost();
        host.RagIndex.Hits.AddRange(
        [
            Hit("doc-1", "qdrant-benchmarks.pdf", "p.4", "sqlite-vec wins at this scale", 0.89),
            Hit("doc-2", "notes.md", null, "single-file stack", 0.77),
        ]);

        var result = await new RagTool(Gert.Testing.Proof.Validation).ExecuteAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"query\":\"qdrant\",\"k\":5}" },
            host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("qdrant-benchmarks.pdf");

        // The shape: ordinal-numbered hits + a document citation per hit, in rank order.
        result.Citations.Should().HaveCount(2);
        result.Citations[0].SourceType.Should().Be(CitationSourceType.Document);
        result.Citations[0].Label.Should().Be("qdrant-benchmarks.pdf - p.4");
        result.Citations[0].DocId.Should().Be("doc-1");
        result.Citations[0].Score.Should().Be(0.89);
        result.Citations[1].Label.Should().Be("notes.md");
    }

    [Fact]
    public async Task RagTool_surfaces_the_decoded_hit_title_in_the_payload_and_citation()
    {
        // The host's ProjectRagResource already decodes documents.filename (base64
        // display metadata) into the hit Title; the tool surfaces that decoded name
        // verbatim in the card label, the citation, and the model payload.
        var host = new FakeToolHost();
        host.RagIndex.Hits.Add(Hit("doc-1", "brewery-notes.md", null, "tripel ferments at 21C", 0.9));

        var result = await new RagTool(Gert.Testing.Proof.Validation).ExecuteAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"query\":\"tripel\"}" },
            host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("brewery-notes.md");
        result.Citations.Single().Label.Should().Be("brewery-notes.md");
    }

    [Fact]
    public async Task RagTool_rejects_missing_query()
    {
        var host = new FakeToolHost();
        var result = await new RagTool(Gert.Testing.Proof.Validation).ExecuteAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{}" }, host);

        // The empty query now fails arg validation (the typed-args base), so the
        // exact message is the validator's - assert the call failed, not its text.
        result.Success.Should().BeFalse();

        // Fail-closed before any RAG search.
        host.RagIndex.Searches.Should().Be(0);
    }

    [Fact]
    public async Task WebSearchTool_returns_web_citations_from_the_search_port()
    {
        var tool = new WebSearchTool(Gert.Testing.Proof.Validation, new FakeWebSearch());

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
        var tool = new WebSearchTool(Gert.Testing.Proof.Validation, new FakeWebSearch());

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{}" });

        // The empty query now fails arg validation (the typed-args base): the call
        // fails before the search port is touched; the message is the validator's.
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task SandboxTool_success_returns_stdout_and_exit_code()
    {
        var tool = new PythonSandboxTool(Gert.Testing.Proof.Validation, StubPythonSandbox.WithStdout("4\n"));

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
        var tool = new PythonSandboxTool(Gert.Testing.Proof.Validation, StubPythonSandbox.ThatFails("Traceback: boom", exitCode: 1));

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
        var tool = new PythonSandboxTool(Gert.Testing.Proof.Validation, StubPythonSandbox.ThatThrows(new InvalidOperationException("runsc spawn failed")));

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
        var tool = new PythonSandboxTool(Gert.Testing.Proof.Validation, StubPythonSandbox.ThatTimesOut());

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
        var tool = new PythonSandboxTool(Gert.Testing.Proof.Validation, StubPythonSandbox.WithStdout("4\n"));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"code\":\"print(2 + 2)\"}",
        });

        // The display seam: the card's pre block renders Stdout verbatim.
        result.Stdout.Should().Be("4\n");
    }

    [Fact]
    public async Task RagTool_clamps_k_to_the_number_of_hits_the_resource_returns()
    {
        // The model asks for k=5; the resource returns two. The tool shapes exactly
        // what came back, in rank order, ordinal-numbered.
        var host = new FakeToolHost();
        host.RagIndex.Hits.AddRange(
        [
            Hit("doc-1", "first.md", "p.1", "alpha", 0.9),
            Hit("doc-2", "second.md", null, "beta", 0.5),
        ]);

        var result = await new RagTool(Gert.Testing.Proof.Validation).ExecuteAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"query\":\"x\",\"k\":5}" },
            host);

        result.Success.Should().BeTrue();
        result.Citations.Should().HaveCount(2);
        result.Citations[0].Ordinal.Should().Be(1);
        result.Citations[1].Ordinal.Should().Be(2);
    }

    [Fact]
    public async Task TodoTool_accepts_the_full_list_and_surfaces_it_for_the_card()
    {
        var tool = new TodoTool(Gert.Testing.Proof.Validation);

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

        // The display seam the todo card renders...
        result.Todos.Should().HaveCount(3);
        result.Todos![0].Should().Be(new TodoItem { Text = "Order the new SSD", Status = TodoStatus.Done });
        result.Todos[1].Status.Should().Be(TodoStatus.Active);
        result.Todos[2].Status.Should().Be(TodoStatus.Pending);

        // ...and the model-facing echo (snake_case statuses) for the follow-up call.
        result.ResultJson.Should().Contain("\"status\":\"active\"").And.Contain("Migrate rag.db");

        // An open list carries the keep-going nudge (2 steps not done)...
        result.ResultJson.Should().Contain("2 step(s) remain");

        // ...and a finished list says to wrap up instead.
        var done = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"todos\":[{\"text\":\"Order the new SSD\",\"status\":\"done\"}]}",
        });
        done.ResultJson.Should().Contain("All steps are done");
    }

    [Fact]
    public async Task TodoTool_rejects_a_missing_list_an_empty_text_and_a_bad_status()
    {
        var tool = new TodoTool(Gert.Testing.Proof.Validation);

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
        var tool = new TodoTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{not json" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid arguments");
    }

    [Fact]
    public void TodoTool_revives_an_open_list_as_a_tail_reminder()
    {
        // IToolReminder: an open snapshot becomes the formatted revival block.
        const string snapshot = """{"todos":[{"text":"step 2","status":"pending"}]}""";

        new TodoTool(Gert.Testing.Proof.Validation).BuildTailReminder(snapshot).Should().Be(TodoTool.CrossTurnReminder(snapshot));
    }

    [Theory]
    [InlineData(null)] // no prior accepted call
    [InlineData("")] // blank snapshot
    [InlineData("""{"todos":[{"text":"step 1","status":"done"}]}""")] // all done
    [InlineData("""{"todos":[]}""")] // empty list
    [InlineData("{not json")] // a parse bug must not throw
    [InlineData("""{"other":1}""")] // right JSON, wrong shape
    public void TodoTool_revives_nothing_when_there_is_no_open_work(string? snapshot)
    {
        new TodoTool(Gert.Testing.Proof.Validation).BuildTailReminder(snapshot).Should().BeNull();
    }

    /// <summary>The pinned instant the clock tests read through TimeProvider.</summary>
    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    [Fact]
    public async Task ClockTool_returns_the_pinned_instant_in_utc()
    {
        var instant = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
        var tool = new ClockTool(Gert.Testing.Proof.Validation, new FixedTime(instant));

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
        var tool = new ClockTool(Gert.Testing.Proof.Validation, new FixedTime(instant));

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
    public async Task ClockTool_defaults_to_the_invocations_client_timezone()
    {
        var instant = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
        var tool = new ClockTool(Gert.Testing.Proof.Validation, new FixedTime(instant));

        // No timezone argument: the browser-supplied snapshot on the invocation
        // makes the answer user-local.
        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{}",
            ClientTimezone = "Europe/Amsterdam",
        });

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("2026-06-05T14:30:00")
            .And.Contain("\"timezone\":\"Europe/Amsterdam\"");
    }

    [Fact]
    public async Task ClockTool_explicit_timezone_argument_beats_the_client_default()
    {
        var instant = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
        var tool = new ClockTool(Gert.Testing.Proof.Validation, new FixedTime(instant));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"timezone\":\"UTC\"}",
            ClientTimezone = "Europe/Amsterdam",
        });

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"timezone\":\"UTC\"")
            .And.Contain("2026-06-05T12:30:00");
    }

    [Fact]
    public async Task ClockTool_unknown_timezone_is_a_graceful_failure()
    {
        var tool = new ClockTool(Gert.Testing.Proof.Validation, new FixedTime(DateTimeOffset.UnixEpoch));

        var result = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"timezone\":\"Mars/Olympus_Mons\"}",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Mars/Olympus_Mons");
    }

    private static RagSearchHit Hit(
        string docId, string title, string? page, string content, double score) => new()
    {
        DocId = docId,
        Title = title,
        Kind = "document",
        Page = page,
        Score = score,
        Content = content,
    };
}
