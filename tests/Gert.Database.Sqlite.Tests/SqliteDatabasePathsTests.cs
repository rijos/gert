using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Gert.Database.Sqlite;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>SqliteDatabasePaths key derivation and pid validation / traversal rejection.</summary>
public class SqliteDatabasePathsTests
{
    [Fact]
    public void Key_is_lowercase_hex_sha256_of_iss_newline_sub()
    {
        const string iss = "https://id.test.local";
        const string sub = "alice";

        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"))).ToLowerInvariant();

        SqliteDatabasePaths.Key(iss, sub).Should().Be(expected);
    }

    [Fact]
    public void Distinct_subjects_yield_distinct_keys()
    {
        SqliteDatabasePaths.Key("iss", "alice").Should().NotBe(SqliteDatabasePaths.Key("iss", "bob"));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void Valid_pid_resolves_under_user_root(string pid)
    {
        using var root = new TempDataRoot();
        var paths = ProviderFixture.PathsFor(root);

        var projectRoot = paths.ProjectRoot(ProviderFixture.ExpectedIssuer, "alice", pid);

        var userRoot = Path.GetFullPath(paths.Root(ProviderFixture.ExpectedIssuer, "alice"));
        Path.GetFullPath(projectRoot).Should().StartWith(userRoot);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    [InlineData("not-a-uuid")]
    [InlineData("default/../../other")]
    [InlineData("/abs/path")]
    [InlineData("")]
    public void Malformed_or_traversing_pid_is_rejected(string pid)
    {
        using var root = new TempDataRoot();
        var paths = ProviderFixture.PathsFor(root);

        var act = () => paths.ProjectRoot(ProviderFixture.ExpectedIssuer, "alice", pid);

        act.Should().Throw<ArgumentException>();
    }
}
