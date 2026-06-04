using System.Text.Json;
using FluentAssertions;
using Gert.Database.Sqlite;
using Gert.Model.Chat;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// U5 security-critical behaviour: the fail-closed validate-before-disk gate, the
/// self-healing meta.json sidecar, two-user isolation, and pid traversal rejection.
/// </summary>
public class ProvisioningTests
{
    [Fact]
    public async Task Unexpected_issuer_is_rejected_before_any_directory_is_created()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);

        var act = async () => await provider.EnsureProvisionedAsync("https://evil.example", "alice");

        await act.Should().ThrowAsync<UnauthorizedIdentityException>();

        // Validate-before-disk: not even the users/ tree may appear.
        Directory.Exists(root.UsersDir).Should().BeFalse();
        DirectoryIsEmptyOrAbsent(root.Path).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/slash")]
    [InlineData("../traversal")]
    [InlineData("emoji-\U0001F4A5")]
    public async Task Malformed_sub_is_rejected_before_any_directory_is_created(string badSub)
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);

        var act = async () => await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, badSub);

        await act.Should().ThrowAsync<UnauthorizedIdentityException>();
        Directory.Exists(root.UsersDir).Should().BeFalse();
    }

    [Fact]
    public async Task Provisioning_creates_user_default_project_and_chat_db()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        Directory.Exists(paths.Root(ProviderFixture.ExpectedIssuer, "alice")).Should().BeTrue();
        File.Exists(paths.MetaFile(ProviderFixture.ExpectedIssuer, "alice")).Should().BeTrue();
        File.Exists(paths.SettingsFile(ProviderFixture.ExpectedIssuer, "alice")).Should().BeTrue();
        File.Exists(paths.ProjectMeta(ProviderFixture.ExpectedIssuer, "alice", "default")).Should().BeTrue();
        File.Exists(paths.ChatDb(ProviderFixture.ExpectedIssuer, "alice", "default")).Should().BeTrue();
        Directory.Exists(paths.FilesDir(ProviderFixture.ExpectedIssuer, "alice", "default")).Should().BeTrue();
        Directory.Exists(paths.MemoryDir(ProviderFixture.ExpectedIssuer, "alice", "default")).Should().BeTrue();

        // rag.db is provisioned alongside chat.db (U4b — vec0 + FTS5 migrated).
        File.Exists(paths.RagDb(ProviderFixture.ExpectedIssuer, "alice", "default")).Should().BeTrue();
    }

    [Fact]
    public async Task Provisioning_is_idempotent()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");
        var act = async () => await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Truncated_meta_json_is_healed_on_next_touch()
    {
        // meta.json is a descriptive sidecar, not a gate: the identity is trusted once
        // the JWT validates. A 0-byte file (an interrupted write before WriteJsonAsync
        // was atomic, or external truncation) must be rewritten from the token on the
        // next touch — never an unhandled JsonException 500ing every request.
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");
        var metaFile = paths.MetaFile(ProviderFixture.ExpectedIssuer, "alice");
        await File.WriteAllTextAsync(metaFile, "");

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        var healed = JsonSerializer.Deserialize<UserMeta>(
            await File.ReadAllTextAsync(metaFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        healed.Should().NotBeNull();
        healed!.Iss.Should().Be(ProviderFixture.ExpectedIssuer);
        healed.Sub.Should().Be("alice");
    }

    [Fact]
    public async Task Healthy_meta_json_is_left_alone_on_subsequent_touches()
    {
        // Re-provisioning must not rewrite a readable meta.json (created_at survives).
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");
        var metaFile = paths.MetaFile(ProviderFixture.ExpectedIssuer, "alice");
        var original = await File.ReadAllTextAsync(metaFile);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        (await File.ReadAllTextAsync(metaFile)).Should().Be(original);
    }

    [Fact]
    public async Task Two_users_get_distinct_folders_and_isolated_chat_dbs()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "bob");

        var aliceRoot = paths.Root(ProviderFixture.ExpectedIssuer, "alice");
        var bobRoot = paths.Root(ProviderFixture.ExpectedIssuer, "bob");
        aliceRoot.Should().NotBe(bobRoot, "distinct sub -> distinct sha256(iss+sub) folder");

        Directory.GetDirectories(root.UsersDir).Should().HaveCount(2);

        // Write into Alice's chat.db; Bob's must stay empty.
        await using (var aliceRepo = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, "alice", "default"))
        {
            await aliceRepo.InsertConversationAsync(new Conversation
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "Alice only",
                ModelId = "qwen3",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await using var aliceRead = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, "alice", "default");
        await using var bobRead = await provider.OpenChatAsync(ProviderFixture.ExpectedIssuer, "bob", "default");

        (await aliceRead.ListConversationsAsync()).Should().ContainSingle();
        (await bobRead.ListConversationsAsync()).Should().BeEmpty();
    }

    private static bool DirectoryIsEmptyOrAbsent(string path) =>
        !Directory.Exists(path) || Directory.GetFileSystemEntries(path).Length == 0;
}
