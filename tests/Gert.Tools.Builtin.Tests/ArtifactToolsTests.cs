using FluentAssertions;
using Gert.Model.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Resources;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for the canvas artifact tools (make/edit/read) - each driven through
/// its <see cref="ITool"/> surface against a <see cref="FakeToolHost"/>'s in-memory
/// object store (<see cref="ResourceScope.Chat"/>). These prove the
/// create-or-overwrite-by-name contract (id preserved, version bumped), the
/// str_replace one-match rule (the feedback loop), and that an edit/make rides the
/// artifact back on <see cref="ToolResult.Artifacts"/> for the orchestrator to emit.
/// </summary>
public sealed class ArtifactToolsTests
{
    private const string Conv = "conv-1";

    private static ToolInvocation Inv(string argsJson) => new()
    {
        Pid = "default",
        ArgumentsJson = argsJson,
        ConversationId = Conv,
        MessageId = "msg-1",
    };

    /// <summary>Seed an object into the host's store under its name (md by default).</summary>
    private static async Task<StoredObject> SeedAsync(
        FakeToolHost host, string name, string content, string kind = "md") =>
        await host.ObjectStore.PutAsync(
            ResourceScope.Chat, new ObjectWrite { Name = name, Content = content, Kind = kind }, default);

    [Fact]
    public async Task Make_creates_a_new_artifact_when_the_name_is_free()
    {
        var host = new FakeToolHost();
        var tool = new MakeArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"index.html\",\"format\":\"html\",\"content\":\"<h1>hi</h1>\"}"), host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("created");

        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "index.html");
        stored.Should().NotBeNull();
        stored!.Content.Should().Be("<h1>hi</h1>");
        stored.Kind.Should().Be("html");
        stored.Version.Should().Be(1);

        var artifact = result.Artifacts.Should().ContainSingle().Subject;
        artifact.Name.Should().Be("index.html");
        artifact.Kind.Should().Be(ArtifactKind.Html);
        artifact.Content.Should().Be("<h1>hi</h1>");
        artifact.ConversationId.Should().Be(Conv);
        artifact.Id.Should().Be(stored.Id);
    }

    [Fact]
    public async Task Make_overwrites_an_existing_name_keeping_id_and_bumping_version()
    {
        var host = new FakeToolHost();
        var seeded = await SeedAsync(host, "notes.md", "old");

        var tool = new MakeArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"notes.md\",\"format\":\"markdown\",\"content\":\"# new\"}"), host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("updated");

        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "notes.md");
        stored!.Id.Should().Be(seeded.Id, "an overwrite keeps the original id");
        stored.Content.Should().Be("# new");
        stored.Version.Should().Be(2);

        result.Artifacts!.Single().Id.Should().Be(seeded.Id);
        result.Artifacts!.Single().Version.Should().Be(2);
    }

    [Fact]
    public async Task Make_rejects_an_unknown_format()
    {
        var host = new FakeToolHost();
        var tool = new MakeArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"a.cob\",\"format\":\"cobol\",\"content\":\"DISPLAY 1.\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("format");
        (await host.ObjectStore.ListAsync(ResourceScope.Chat)).Should().BeEmpty();
    }

    [Fact]
    public async Task Make_rejects_empty_content()
    {
        var host = new FakeToolHost();
        var tool = new MakeArtifactTool(Gert.Testing.Proof.Validation);

        // The fail-closed validator (not the tool) rejects empty content.
        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"a.md\",\"format\":\"markdown\",\"content\":\"\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("content");
        (await host.ObjectStore.ListAsync(ResourceScope.Chat)).Should().BeEmpty();
    }

    [Fact]
    public async Task Make_accepts_a_short_format_alias()
    {
        var host = new FakeToolHost();
        var tool = new MakeArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(Inv("{\"name\":\"s.py\",\"format\":\"py\",\"content\":\"print(1)\"}"), host);

        result.Success.Should().BeTrue();
        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "s.py");
        stored!.Kind.Should().Be("py");
        result.Artifacts!.Single().Kind.Should().Be(ArtifactKind.Py);
    }

    [Fact]
    public async Task Edit_replaces_a_unique_snippet_and_bumps_version()
    {
        var host = new FakeToolHost();
        var seeded = await SeedAsync(host, "page.html", "<h1>Old Title</h1>", "html");

        var tool = new EditArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"page.html\",\"old_str\":\"Old Title\",\"new_str\":\"New Title\"}"), host);

        result.Success.Should().BeTrue();

        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "page.html");
        stored!.Content.Should().Be("<h1>New Title</h1>");
        stored.Version.Should().Be(2);
        stored.Id.Should().Be(seeded.Id);

        result.Artifacts!.Single().Id.Should().Be(seeded.Id);
        result.Artifacts!.Single().Content.Should().Be("<h1>New Title</h1>");
    }

    [Fact]
    public async Task Edit_with_no_match_errors_and_does_not_write()
    {
        var host = new FakeToolHost();
        await SeedAsync(host, "page.html", "<h1>Title</h1>", "html");

        var tool = new EditArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(
            Inv("{\"name\":\"page.html\",\"old_str\":\"absent\",\"new_str\":\"x\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");

        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "page.html");
        stored!.Content.Should().Be("<h1>Title</h1>", "a no-match edit must not write");
        stored.Version.Should().Be(1);
    }

    [Fact]
    public async Task Edit_with_multiple_matches_errors_and_asks_for_context()
    {
        var host = new FakeToolHost();
        await SeedAsync(host, "a.md", "x\nx\n");

        var tool = new EditArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\",\"old_str\":\"x\",\"new_str\":\"y\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("2");

        var stored = await host.ObjectStore.GetAsync(ResourceScope.Chat, "a.md");
        stored!.Version.Should().Be(1, "an ambiguous edit must not write");
    }

    [Fact]
    public async Task Edit_of_an_unknown_artifact_errors()
    {
        var host = new FakeToolHost();
        var tool = new EditArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(Inv("{\"name\":\"ghost.md\",\"old_str\":\"a\",\"new_str\":\"b\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no artifact named 'ghost.md'");
    }

    [Fact]
    public async Task Read_returns_line_numbered_content()
    {
        var host = new FakeToolHost();
        await SeedAsync(host, "a.md", "first\nsecond\nthird");

        var tool = new ReadArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\"}"), host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("1\\tfirst").And.Contain("3\\tthird")
            .And.Contain("\"line_count\":3");
    }

    [Fact]
    public async Task Read_honors_a_line_range()
    {
        var host = new FakeToolHost();
        await SeedAsync(host, "a.md", "l1\nl2\nl3\nl4");

        var tool = new ReadArtifactTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\",\"range\":[2,3]}"), host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("2\\tl2").And.Contain("3\\tl3")
            .And.NotContain("1\\tl1").And.NotContain("4\\tl4");
    }

    [Theory]
    [InlineData("{\"name\":\"a.md\",\"range\":[\"two\",\"three\"]}")] // strings, not ints
    [InlineData("{\"name\":\"a.md\",\"range\":[1.5,3.5]}")] // non-integer numbers
    [InlineData("{\"name\":\"a.md\",\"range\":[null,3]}")] // null entry
    public async Task Read_with_a_malformed_range_is_a_tool_error_never_a_throw(string argsJson)
    {
        var host = new FakeToolHost();
        var tool = new ReadArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(Inv(argsJson), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("range");
    }

    [Fact]
    public async Task Read_of_an_unknown_artifact_errors()
    {
        var host = new FakeToolHost();
        var tool = new ReadArtifactTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(Inv("{\"name\":\"ghost.md\"}"), host);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no artifact named 'ghost.md'");
    }

    [Fact]
    public async Task List_returns_every_artifact_with_its_format_and_version()
    {
        var host = new FakeToolHost();
        await SeedAsync(host, "page.html", "<h1>hi</h1>", "html");
        // A second put under the same name bumps the version - the listing must reflect it.
        await SeedAsync(host, "notes.md", "v1");
        await SeedAsync(host, "notes.md", "v2");

        var tool = new ListArtifactsTool(Gert.Testing.Proof.Validation);
        var result = await tool.ExecuteAsync(Inv("{}"), host);

        result.Success.Should().BeTrue();
        // The format word is the canonical one (html/markdown), not the on-disk token.
        result.ResultJson.Should().Contain("page.html").And.Contain("\"format\":\"html\"")
            .And.Contain("notes.md").And.Contain("\"format\":\"markdown\"")
            .And.Contain("\"version\":2");
        // The human-facing stdout lists each name with its version.
        result.Stdout.Should().Contain("page.html (v1)").And.Contain("notes.md (v2)");
    }

    [Fact]
    public async Task List_on_an_empty_conversation_returns_no_artifacts()
    {
        var host = new FakeToolHost();
        var tool = new ListArtifactsTool(Gert.Testing.Proof.Validation);

        var result = await tool.ExecuteAsync(Inv("{}"), host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"artifacts\":[]");
        result.Stdout.Should().Be("No files yet.");
    }
}
