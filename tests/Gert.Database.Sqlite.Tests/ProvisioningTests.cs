using FluentAssertions;
using Gert.Model.Chat;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Lazy, self-provisioning behaviour: opening a database creates + migrates it on
/// the spot (no separate ensure step, no cache), the request-edge provisioner seeds
/// the username + default project in <c>user.db</c>, and per-project / per-user
/// databases are isolated files.
/// </summary>
public class ProvisioningTests
{
    private static string Iss => ProviderFixture.ExpectedIssuer;

    [Fact]
    public async Task Opening_chat_lazily_creates_and_migrates_the_db()
    {
        await using var root = new TempDataRoot();
        var chat = ProviderFixture.ChatProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        // No prior provisioning: the open self-creates + migrates the file.
        await using var repo = await chat.OpenAsync(Iss, "alice", "default");

        File.Exists(paths.ChatDb(Iss, "alice", "default")).Should().BeTrue();
        (await repo.ListConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Provisioning_seeds_username_and_default_project()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);

        await provider.EnsureProvisionedAsync(Iss, "alice");

        await using var repo = await provider.Users.OpenAsync(Iss, "alice");
        (await repo.GetUsernameAsync()).Should().Be("alice");
        (await repo.GetProjectAsync("default")).Should().NotBeNull();
    }

    [Fact]
    public async Task Two_users_are_isolated_in_separate_files()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        paths.Root(Iss, "alice").Should().NotBe(paths.Root(Iss, "bob"));

        await using (var aliceChat = await provider.OpenChatAsync(Iss, "alice", "default"))
        {
            await aliceChat.InsertConversationAsync(new Conversation
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "alice only",
                ModelId = "qwen3",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await using var aliceRead = await provider.OpenChatAsync(Iss, "alice", "default");
        await using var bobRead = await provider.OpenChatAsync(Iss, "bob", "default");

        (await aliceRead.ListConversationsAsync()).Should().ContainSingle();
        (await bobRead.ListConversationsAsync()).Should().BeEmpty();
    }
}
