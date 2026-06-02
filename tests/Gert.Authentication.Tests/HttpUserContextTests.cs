using System.Security.Claims;
using FluentAssertions;
using Gert.Authentication;
using Gert.Model;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Gert.Authentication.Tests;

/// <summary>
/// Claim → <see cref="Gert.Service.IUserContext"/> mapping for <see cref="HttpUserContext"/>,
/// covering the three standing roles, the <c>gert_tools</c> parsing cases, and the
/// <c>sub</c>/<c>iss</c>/<c>Username</c> rules (auth.md § user context).
/// </summary>
public sealed class HttpUserContextTests
{
    // A registry that knows exactly the three real capability ids.
    private static ToolRegistry Registry() => new(new ITool[]
    {
        new StubTool("rag"),
        new StubTool("search"),
        new StubTool("sandbox"),
    });

    private static HttpUserContext Build(ClaimsPrincipal principal, ToolOptions? options = null)
    {
        var ctx = new DefaultHttpContext { User = principal };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(ctx);

        return new HttpUserContext(
            accessor,
            Registry(),
            Options.Create(options ?? new ToolOptions()));
    }

    /// <summary>
    /// Build a principal the way the validated JWT pipeline would: <c>preferred_username</c>
    /// as the name claim and <c>groups</c> as the role claim (auth.md mappings).
    /// </summary>
    private static ClaimsPrincipal Principal(
        string sub = "user-123",
        string iss = "https://id.test.local",
        string? username = null,
        IEnumerable<string>? groups = null,
        string? gertTools = "rag search")
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("iss", iss),
            new("preferred_username", username ?? sub),
        };
        foreach (var g in groups ?? [])
        {
            claims.Add(new Claim("groups", g));
        }
        if (gertTools is not null)
        {
            claims.Add(new Claim("gert_tools", gertTools));
        }

        var identity = new ClaimsIdentity(
            claims,
            authenticationType: "Test",
            nameType: "preferred_username",
            roleType: "groups");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Admin_role_is_admin_and_gets_all_tools()
    {
        var user = Build(Principal(groups: ["gert-admins"], gertTools: "*"));

        user.IsAdmin.Should().BeTrue();
        user.AllowedTools.Should().BeEquivalentTo("rag", "search", "sandbox");
    }

    [Fact]
    public void User_role_is_not_admin_and_sandbox_is_excluded()
    {
        var user = Build(Principal(groups: ["gert-users"], gertTools: "rag search"));

        user.IsAdmin.Should().BeFalse();
        user.AllowedTools.Should().BeEquivalentTo("rag", "search");
        user.CanUseTool("sandbox").Should().BeFalse();
        user.CanUseTool("rag").Should().BeTrue();
    }

    [Fact]
    public void Limited_role_gets_only_rag()
    {
        var user = Build(Principal(groups: ["gert-users"], gertTools: "rag"));

        user.AllowedTools.Should().BeEquivalentTo("rag");
        user.CanUseTool("search").Should().BeFalse();
    }

    [Fact]
    public void Absent_gert_tools_falls_back_to_default_grant()
    {
        var options = new ToolOptions { DefaultGrant = ["rag", "search"] };
        var user = Build(Principal(gertTools: null), options);

        user.AllowedTools.Should().BeEquivalentTo("rag", "search");
    }

    [Fact]
    public void Blank_gert_tools_falls_back_to_default_grant()
    {
        var options = new ToolOptions { DefaultGrant = ["rag"] };
        var user = Build(Principal(gertTools: "   "), options);

        user.AllowedTools.Should().BeEquivalentTo("rag");
    }

    [Fact]
    public void Default_grant_is_intersected_with_registry()
    {
        // A default that names a non-registered id must drop it.
        var options = new ToolOptions { DefaultGrant = ["rag", "ghost"] };
        var user = Build(Principal(gertTools: null), options);

        user.AllowedTools.Should().BeEquivalentTo("rag");
    }

    [Fact]
    public void Star_grants_every_registered_tool()
    {
        var user = Build(Principal(gertTools: "*"));

        user.AllowedTools.Should().BeEquivalentTo("rag", "search", "sandbox");
    }

    [Fact]
    public void Json_array_gert_tools_is_parsed()
    {
        var user = Build(Principal(gertTools: """["rag","search"]"""));

        user.AllowedTools.Should().BeEquivalentTo("rag", "search");
    }

    [Fact]
    public void Comma_delimited_gert_tools_is_parsed()
    {
        var user = Build(Principal(gertTools: "rag, search"));

        user.AllowedTools.Should().BeEquivalentTo("rag", "search");
    }

    [Fact]
    public void Unknown_tool_id_is_dropped_by_normalize()
    {
        var user = Build(Principal(gertTools: "rag bogus sandbox"));

        user.AllowedTools.Should().BeEquivalentTo("rag", "sandbox");
        user.CanUseTool("bogus").Should().BeFalse();
    }

    [Fact]
    public void Sub_absent_throws()
    {
        // Principal with no sub claim.
        var identity = new ClaimsIdentity(
            [new Claim("iss", "https://id.test.local")],
            authenticationType: "Test");
        var user = Build(new ClaimsPrincipal(identity));

        var act = () => user.Sub;
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Iss_is_mapped_from_claim()
    {
        var user = Build(Principal(iss: "https://id.homelab.lan"));

        user.Iss.Should().Be("https://id.homelab.lan");
    }

    [Fact]
    public void Username_is_preferred_username()
    {
        var user = Build(Principal(sub: "abc", username: "gerrit.de.vries"));

        user.Username.Should().Be("gerrit.de.vries");
    }

    [Fact]
    public void Username_falls_back_to_sub_when_name_absent()
    {
        // No preferred_username claim → Identity.Name is null → falls back to Sub.
        var identity = new ClaimsIdentity(
            [new Claim("sub", "abc"), new Claim("iss", "https://id.test.local")],
            authenticationType: "Test",
            nameType: "preferred_username",
            roleType: "groups");
        var user = Build(new ClaimsPrincipal(identity));

        user.Username.Should().Be("abc");
    }

    /// <summary>Minimal <see cref="ITool"/> used only to populate the registry by id.</summary>
    private sealed class StubTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Name => Id;
        public ToolKind Kind => ToolKind.Rag;
        public string Description => Id;
        public string ParametersSchema => "{}";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true });
    }
}
