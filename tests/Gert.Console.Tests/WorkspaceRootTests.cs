using FluentAssertions;
using Gert.Console.Tools;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The TUI workspace confinement (U16): every file-tool path resolves through
/// <see cref="WorkspaceRoot.ResolveSafe"/>, which must accept anything inside
/// the launch directory and reject every way of escaping it.
/// </summary>
public sealed class WorkspaceRootTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceRoot _workspace;

    public WorkspaceRootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gert-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        _workspace = new WorkspaceRoot(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Resolves_a_relative_path_inside_the_root()
    {
        _workspace.ResolveSafe("sub/file.txt")
            .Should().Be(Path.Combine(_root, "sub", "file.txt"));
    }

    [Fact]
    public void Resolves_the_root_itself()
    {
        _workspace.ResolveSafe(".").Should().Be(_workspace.Root);
    }

    [Fact]
    public void Accepts_an_absolute_path_already_inside_the_root()
    {
        var inside = Path.Combine(_root, "sub", "x.cs");

        _workspace.ResolveSafe(inside).Should().Be(inside);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("..")]
    public void Rejects_dotdot_traversal(string path)
    {
        var act = () => _workspace.ResolveSafe(path);

        act.Should().Throw<ArgumentException>().WithMessage("*escapes the workspace*");
    }

    [Fact]
    public void Rejects_an_absolute_path_outside_the_root()
    {
        var act = () => _workspace.ResolveSafe(Path.GetTempPath());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_sibling_directory_sharing_the_root_prefix()
    {
        // /tmp/gert-ws-X vs /tmp/gert-ws-X-evil — the ordinal prefix check must
        // compare path SEGMENTS, not raw characters.
        var act = () => _workspace.ResolveSafe(_root + "-evil/file.txt");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_input(string path)
    {
        var act = () => _workspace.ResolveSafe(path);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToRelative_maps_back_to_workspace_relative_display_form()
    {
        var abs = Path.Combine(_root, "sub", "file.txt");

        _workspace.ToRelative(abs).Should().Be(Path.Combine("sub", "file.txt"));
        _workspace.ToRelative(_workspace.Root).Should().Be(".");
    }
}
