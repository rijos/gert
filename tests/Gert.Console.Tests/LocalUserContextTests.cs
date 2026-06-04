using FluentAssertions;
using Gert.Console;
using Gert.Service.Tools;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The Console's single fixed local user (testing.md §7, tech-stack.md
/// § Architecture): a stable identity, admin, and the blanket tool grant
/// (<c>"*"</c>) — every registered capability id.
/// </summary>
public sealed class LocalUserContextTests
{
    // The built-in capability ids AddGertServices registers (rag / search / sandbox).
    private static readonly string[] BuiltInToolIds = ["rag", "search", "sandbox"];

    private static LocalUserContext NewContext() =>
        new(new ToolRegistry(BuiltInToolIds));

    [Fact]
    public void Has_a_stable_identity_and_is_admin()
    {
        var user = NewContext();

        user.Iss.Should().Be("gert-console");
        user.Sub.Should().Be("local");
        user.Username.Should().Be("local");
        user.IsAdmin.Should().BeTrue();
    }

    [Fact]
    public void Allowed_tools_is_every_registered_id_the_blanket_grant()
    {
        var user = NewContext();

        user.AllowedTools.Should().BeEquivalentTo(BuiltInToolIds);
    }

    [Fact]
    public void Can_use_every_registered_tool()
    {
        var user = NewContext();

        foreach (var id in BuiltInToolIds)
        {
            user.CanUseTool(id).Should().BeTrue($"'{id}' is a registered, granted tool");
        }
    }

    [Fact]
    public void Cannot_use_an_unknown_tool_id()
    {
        var user = NewContext();

        user.CanUseTool("not-a-real-tool").Should().BeFalse();
    }
}
