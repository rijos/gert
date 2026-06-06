using System.Text.Json;
using FluentAssertions;
using Gert.Console.Tools;
using Gert.Service.Tools;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The TUI's read-only local file tools (U16): read_file / list_dir / glob /
/// grep over a temp workspace. Asserts the <see cref="ToolResult"/> shapes
/// (ResultJson for the model, Stdout for the card) and the SandboxTool failure
/// discipline — bad args, escapes, and missing paths are tool errors, never
/// throws.
/// </summary>
public sealed class LocalToolTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceRoot _workspace;

    public LocalToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gert-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "readme.md"), "# Title\nsome body text\n");
        File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "class Main\n{\n    // TODO fix\n}\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x00, 0x01, 0x02]);
        _workspace = new WorkspaceRoot(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ToolInvocation Invoke(object args) => new()
    {
        Pid = "default",
        ArgumentsJson = JsonSerializer.Serialize(args),
    };

    [Fact]
    public async Task Read_file_returns_content_and_card_summary()
    {
        var tool = new ReadFileTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { path = "readme.md" }));

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("content").GetString().Should().Contain("# Title");
        doc.RootElement.GetProperty("total_lines").GetInt32().Should().BeGreaterThan(1);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        result.Stdout.Should().Contain("readme.md");
    }

    [Fact]
    public async Task Read_file_pages_with_offset_and_limit()
    {
        var tool = new ReadFileTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { path = "src/main.cs", offset = 2, limit = 1 }));

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("content").GetString().Should().Be("{");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("missing.txt")]
    public async Task Read_file_failures_are_tool_errors(string path)
    {
        var tool = new ReadFileTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { path }));

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Invalid_json_arguments_are_tool_errors()
    {
        var tool = new ReadFileTool(_workspace);

        var result = await tool.ExecuteAsync(new ToolInvocation { Pid = "default", ArgumentsJson = "{not json" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid arguments");
    }

    [Fact]
    public async Task List_dir_lists_directories_first()
    {
        var tool = new ListDirTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().StartWith("src/");
        result.Stdout.Should().Contain("readme.md");
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("entries").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Glob_finds_files_by_pattern()
    {
        var tool = new GlobTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { pattern = "**/*.cs" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Be(Path.Combine("src", "main.cs"));
    }

    [Fact]
    public async Task Glob_with_no_matches_reports_cleanly()
    {
        var tool = new GlobTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { pattern = "**/*.py" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Be("(no matches)");
    }

    [Fact]
    public async Task Grep_matches_lines_and_skips_binary_files()
    {
        var tool = new GrepTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { pattern = "TODO" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain("main.cs:3:");
        result.Stdout.Should().NotContain("blob.bin");
        using var doc = JsonDocument.Parse(result.ResultJson!);
        var hit = doc.RootElement.GetProperty("hits")[0];
        hit.GetProperty("line").GetInt32().Should().Be(3);
        hit.GetProperty("text").GetString().Should().Contain("TODO");
    }

    [Fact]
    public async Task Grep_with_invalid_regex_is_a_tool_error()
    {
        var tool = new GrepTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { pattern = "([" }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid regex");
    }

    [Fact]
    public async Task Grep_respects_the_glob_filter()
    {
        var tool = new GrepTool(_workspace);

        var result = await tool.ExecuteAsync(Invoke(new { pattern = ".", glob = "**/*.md" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain("readme.md");
        result.Stdout.Should().NotContain("main.cs");
    }
}
