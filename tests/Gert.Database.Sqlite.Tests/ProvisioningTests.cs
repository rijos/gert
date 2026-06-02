using System.Text.Json;
using FluentAssertions;
using Gert.Database.Sqlite;
using Gert.Model.Chat;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// U5 security-critical behaviour: the fail-closed validate-before-disk gate, the
/// meta.json identity binding, two-user isolation, and pid traversal rejection.
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
    public async Task Tampered_identity_binding_on_existing_folder_is_refused()
    {
        // Simulate the reuse attack: the folder key is sha256(iss + "\n" + sub),
        // so a folder is addressed purely by (iss, sub). We provision a folder, then
        // overwrite its meta.json so the recorded (iss, sub) differs from the token
        // that resolves to that very folder — exactly what a recreated/reassigned
        // identity colliding onto an existing folder would look like. The next
        // EnsureProvisioned for that (iss, sub) must refuse rather than serve it.
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        var metaFile = paths.MetaFile(ProviderFixture.ExpectedIssuer, "alice");
        var tampered = new UserMeta
        {
            Iss = ProviderFixture.ExpectedIssuer,
            Sub = "someone-else", // binding now disagrees with the resolving token.
            Username = "someone-else",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            SchemaVersion = 1,
        };
        await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(tampered));

        var act = async () => await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, "alice");

        await act.Should().ThrowAsync<IdentityBindingException>();
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
