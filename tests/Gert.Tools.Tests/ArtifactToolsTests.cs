using FluentAssertions;
using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service;
using Gert.Service.Tools;
using Gert.Testing.Fakes;
using Gert.Tools.Builtin;
using NSubstitute;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// Unit tests for the canvas artifact tools (make/edit/read) - each driven through
/// its <see cref="ITool"/> surface against an NSubstitute <see cref="IChatRepository"/>.
/// These prove the create-or-overwrite-by-name contract, the str_replace one-match
/// rule (the feedback loop), and that an edit/make rides the artifact back on
/// <see cref="ToolResult.Artifacts"/> for the orchestrator to emit.
/// </summary>
public sealed class ArtifactToolsTests
{
    private const string Conv = "conv-1";

    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static readonly TimeProvider Time =
        new FixedTime(new DateTimeOffset(2026, 6, 7, 9, 0, 0, TimeSpan.Zero));

    private static readonly IUserContext User = new TestUserContext();

    // The tools open chat.db per-use via the provider (the RagTool pattern), so a
    // test backs that provider with its configured repo substitute.
    private static IChatDatabaseProvider Provider(IChatRepository repo)
    {
        var provider = Substitute.For<IChatDatabaseProvider>();
        provider.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(repo);
        return provider;
    }

    private static ToolInvocation Inv(string argsJson) => new()
    {
        Pid = "default",
        ArgumentsJson = argsJson,
        ConversationId = Conv,
        MessageId = "msg-1",
    };

    private static Artifact Existing(string name, string content, ArtifactKind kind = ArtifactKind.Md, int version = 1) => new()
    {
        Id = "art-1",
        ConversationId = Conv,
        MessageId = "msg-0",
        Kind = kind,
        Name = name,
        Language = null,
        Content = content,
        Version = version,
        CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Make_creates_a_new_artifact_when_the_name_is_free()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "index.html", Arg.Any<CancellationToken>()).Returns((Artifact?)null);

        var tool = new MakeArtifactTool(Provider(repo), User, Time);
        var result = await tool.ExecuteAsync(Inv(
            "{\"name\":\"index.html\",\"format\":\"html\",\"content\":\"<h1>hi</h1>\"}"));

        result.Success.Should().BeTrue();
        await repo.Received(1).InsertArtifactAsync(
            Arg.Is<Artifact>(a => a.Name == "index.html" && a.Kind == ArtifactKind.Html
                && a.Content == "<h1>hi</h1>" && a.ConversationId == Conv && a.Version == 1),
            Arg.Any<CancellationToken>());
        await repo.DidNotReceive().UpdateArtifactAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
        result.Artifacts.Should().ContainSingle();
        result.ResultJson.Should().Contain("created");
    }

    [Fact]
    public async Task Make_overwrites_an_existing_name_keeping_id_and_bumping_version()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "notes.md", Arg.Any<CancellationToken>())
            .Returns(Existing("notes.md", "old", ArtifactKind.Md, version: 2));

        var tool = new MakeArtifactTool(Provider(repo), User, Time);
        var result = await tool.ExecuteAsync(Inv(
            "{\"name\":\"notes.md\",\"format\":\"markdown\",\"content\":\"# new\"}"));

        result.Success.Should().BeTrue();
        await repo.Received(1).UpdateArtifactAsync(
            Arg.Is<Artifact>(a => a.Id == "art-1" && a.Content == "# new" && a.Version == 3),
            Arg.Any<CancellationToken>());
        await repo.DidNotReceive().InsertArtifactAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
        result.Artifacts!.Single().Id.Should().Be("art-1");
        result.ResultJson.Should().Contain("updated");
    }

    [Fact]
    public async Task Make_rejects_an_unknown_format()
    {
        var repo = Substitute.For<IChatRepository>();
        var tool = new MakeArtifactTool(Provider(repo), User, Time);

        var result = await tool.ExecuteAsync(Inv(
            "{\"name\":\"a.cob\",\"format\":\"cobol\",\"content\":\"DISPLAY 1.\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("format");
        await repo.DidNotReceive().InsertArtifactAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Make_rejects_empty_content_and_missing_conversation()
    {
        var repo = Substitute.For<IChatRepository>();
        var tool = new MakeArtifactTool(Provider(repo), User, Time);

        var empty = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\",\"format\":\"markdown\",\"content\":\"\"}"));
        empty.Success.Should().BeFalse();
        empty.Error.Should().Contain("content");

        var noConv = await tool.ExecuteAsync(new ToolInvocation
        {
            Pid = "default",
            ArgumentsJson = "{\"name\":\"a.md\",\"format\":\"markdown\",\"content\":\"x\"}",
        });
        noConv.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Make_accepts_a_short_format_alias()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "s.py", Arg.Any<CancellationToken>()).Returns((Artifact?)null);
        var tool = new MakeArtifactTool(Provider(repo), User, Time);

        var result = await tool.ExecuteAsync(Inv("{\"name\":\"s.py\",\"format\":\"py\",\"content\":\"print(1)\"}"));

        result.Success.Should().BeTrue();
        await repo.Received(1).InsertArtifactAsync(
            Arg.Is<Artifact>(a => a.Kind == ArtifactKind.Py), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_replaces_a_unique_snippet_and_bumps_version()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "page.html", Arg.Any<CancellationToken>())
            .Returns(Existing("page.html", "<h1>Old Title</h1>", ArtifactKind.Html));

        var tool = new EditArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv(
            "{\"name\":\"page.html\",\"old_str\":\"Old Title\",\"new_str\":\"New Title\"}"));

        result.Success.Should().BeTrue();
        await repo.Received(1).UpdateArtifactAsync(
            Arg.Is<Artifact>(a => a.Content == "<h1>New Title</h1>" && a.Version == 2),
            Arg.Any<CancellationToken>());
        result.Artifacts!.Single().Id.Should().Be("art-1");
    }

    [Fact]
    public async Task Edit_with_no_match_errors_and_does_not_write()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "page.html", Arg.Any<CancellationToken>())
            .Returns(Existing("page.html", "<h1>Title</h1>", ArtifactKind.Html));

        var tool = new EditArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv(
            "{\"name\":\"page.html\",\"old_str\":\"absent\",\"new_str\":\"x\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        await repo.DidNotReceive().UpdateArtifactAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_with_multiple_matches_errors_and_asks_for_context()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "a.md", Arg.Any<CancellationToken>())
            .Returns(Existing("a.md", "x\nx\n", ArtifactKind.Md));

        var tool = new EditArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\",\"old_str\":\"x\",\"new_str\":\"y\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("2");
        await repo.DidNotReceive().UpdateArtifactAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_of_an_unknown_artifact_errors()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "ghost.md", Arg.Any<CancellationToken>()).Returns((Artifact?)null);

        var tool = new EditArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"ghost.md\",\"old_str\":\"a\",\"new_str\":\"b\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no artifact named 'ghost.md'");
    }

    [Fact]
    public async Task Read_returns_line_numbered_content()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "a.md", Arg.Any<CancellationToken>())
            .Returns(Existing("a.md", "first\nsecond\nthird", ArtifactKind.Md));

        var tool = new ReadArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("1\\tfirst").And.Contain("3\\tthird")
            .And.Contain("\"line_count\":3");
    }

    [Fact]
    public async Task Read_honors_a_line_range()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "a.md", Arg.Any<CancellationToken>())
            .Returns(Existing("a.md", "l1\nl2\nl3\nl4", ArtifactKind.Md));

        var tool = new ReadArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"a.md\",\"range\":[2,3]}"));

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
        var repo = Substitute.For<IChatRepository>();
        var tool = new ReadArtifactTool(Provider(repo), User);

        var result = await tool.ExecuteAsync(Inv(argsJson));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("range");
        // Fails before any chat.db open, like the other argument errors.
        await repo.DidNotReceive().GetArtifactByNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Read_of_an_unknown_artifact_errors()
    {
        var repo = Substitute.For<IChatRepository>();
        repo.GetArtifactByNameAsync(Conv, "ghost.md", Arg.Any<CancellationToken>()).Returns((Artifact?)null);

        var tool = new ReadArtifactTool(Provider(repo), User);
        var result = await tool.ExecuteAsync(Inv("{\"name\":\"ghost.md\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no artifact named 'ghost.md'");
    }
}
