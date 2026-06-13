using FluentAssertions;
using Gert.Model;
using Gert.Model.Projects;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Round-trips <c>user.db</c> via <see cref="Gert.Database.IUserRepository"/> -- the
/// durable, transactional home of the username (admin scan), user settings, and the
/// project registry. Self-provisions on open.
/// </summary>
public class SqliteUserRepositoryTests
{
    private const string Sub = "user-sub";

    private static string Iss => ProviderFixture.ExpectedIssuer;

    [Fact]
    public async Task Username_is_absent_then_round_trips()
    {
        await using var root = new TempDataRoot();
        var users = ProviderFixture.UserProviderFor(root);

        await using var repo = await users.OpenAsync(Iss, Sub);
        (await repo.GetUsernameAsync()).Should().BeNull("nothing has been seeded yet");

        await repo.SetUsernameAsync("alice");
        (await repo.GetUsernameAsync()).Should().Be("alice");

        // Upsert, not insert - a second set replaces.
        await repo.SetUsernameAsync("alice-renamed");
        (await repo.GetUsernameAsync()).Should().Be("alice-renamed");
    }

    [Fact]
    public async Task Settings_default_then_round_trips()
    {
        await using var root = new TempDataRoot();
        var users = ProviderFixture.UserProviderFor(root);

        await using var repo = await users.OpenAsync(Iss, Sub);
        (await repo.GetSettingsAsync()).Should().BeEquivalentTo(new UserSettings());

        await repo.SaveSettingsAsync(new UserSettings { ReplyLanguage = "nl", Theme = Theme.Dark });

        var reloaded = await repo.GetSettingsAsync();
        reloaded.ReplyLanguage.Should().Be("nl");
        reloaded.Theme.Should().Be(Theme.Dark);
    }

    [Fact]
    public async Task Project_registry_save_get_list_delete_round_trips()
    {
        await using var root = new TempDataRoot();
        var users = ProviderFixture.UserProviderFor(root);

        await using var repo = await users.OpenAsync(Iss, Sub);

        var pid = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        await repo.SaveProjectAsync(new ProjectMeta
        {
            Id = pid,
            Name = "Research",
            Description = "notes",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var fetched = await repo.GetProjectAsync(pid);
        fetched!.Name.Should().Be("Research");
        fetched.Description.Should().Be("notes");

        (await repo.ListProjectsAsync()).Select(p => p.Id).Should().Contain(pid);

        (await repo.DeleteProjectAsync(pid)).Should().BeTrue();
        (await repo.GetProjectAsync(pid)).Should().BeNull();
        (await repo.DeleteProjectAsync(pid)).Should().BeFalse("idempotent");
    }
}
