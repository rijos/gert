using System.Text;
using FluentAssertions;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Projects;
using Gert.Service.Storage;
using Gert.Storage;
using Gert.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Integration tests for the four lifecycle services (Projects/Settings/Account/
/// Admin) wired over the real Sqlite adapters and a throwaway
/// <see cref="TempDataRoot"/> — proving the orchestration of
/// <see cref="IUserStore"/> + <see cref="Service.Database.IDatabaseProvider"/> +
/// <see cref="IObjectStore"/>, including default-emptied-not-removed and a
/// non-empty export archive.
/// </summary>
public class LifecycleServicesTests
{
    private const string Sub = "lifecycle-sub";

    private static string Iss => ProviderFixture.ExpectedIssuer;

    private sealed record Harness(
        ProjectService Projects,
        SettingsService Settings,
        AccountService Account,
        AdminService Admin,
        SqliteDatabaseProvider Provider,
        LocalObjectStore Objects,
        SqliteDatabasePaths Paths,
        IUserContext User);

    private static Harness Build(TempDataRoot root, string sub = Sub)
    {
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        var store = ProviderFixture.StoreFor(root);
        var objects = ProviderFixture.ObjectsFor(root);
        var validation = new PassThroughValidationProvider();
        IUserContext user = new FixedUserContext { Sub = sub };

        return new Harness(
            new ProjectService(store, provider, validation, user),
            new SettingsService(store, provider, validation, user),
            new AccountService(store, provider, objects, user),
            new AdminService(store),
            provider,
            objects,
            paths,
            user);
    }

    [Fact]
    public async Task Settings_get_defaults_then_update_merges()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        var defaults = await h.Settings.GetAsync();
        defaults.ReplyLanguage.Should().BeNull();

        var updated = await h.Settings.UpdateAsync(new UpdateSettingsRequest { ReplyLanguage = "nl" });
        updated.ReplyLanguage.Should().Be("nl");

        // Persisted across reads.
        (await h.Settings.GetAsync()).ReplyLanguage.Should().Be("nl");
    }

    [Fact]
    public async Task Project_create_list_get_round_trips_with_counts()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        var created = await h.Projects.CreateAsync(new CreateProjectRequest { Name = "Research" });
        created.Name.Should().Be("Research");
        Guid.TryParseExact(created.Id, "D", out _).Should().BeTrue();

        var list = await h.Projects.ListAsync();
        list.Select(p => p.Meta.Id).Should().Contain([SqliteDatabasePaths.DefaultProjectId, created.Id]);

        var got = await h.Projects.GetAsync(created.Id);
        got!.Meta.Name.Should().Be("Research");
        got.ConversationCount.Should().Be(0);
        got.DocumentCount.Should().Be(0);
        got.MemoryCount.Should().Be(0);
    }

    [Fact]
    public async Task Project_update_merges_partial_fields()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        var created = await h.Projects.CreateAsync(new CreateProjectRequest { Name = "Old", Description = "keep" });

        var updated = await h.Projects.UpdateAsync(created.Id, new UpdateProjectRequest { Name = "New" });
        updated!.Name.Should().Be("New");
        updated.Description.Should().Be("keep", "an omitted field is left unchanged");
    }

    [Fact]
    public async Task Delete_non_default_project_removes_it()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        var created = await h.Projects.CreateAsync(new CreateProjectRequest { Name = "Temp" });
        Directory.Exists(h.Paths.ProjectRoot(Iss, Sub, created.Id)).Should().BeTrue();

        (await h.Projects.DeleteAsync(created.Id)).Should().BeTrue();
        Directory.Exists(h.Paths.ProjectRoot(Iss, Sub, created.Id)).Should().BeFalse();
        (await h.Projects.GetAsync(created.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_default_project_is_emptied_not_removed()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        // Seed a conversation so there is content to clear.
        await using (var chat = await h.Provider.OpenChatAsync(Iss, Sub, SqliteDatabasePaths.DefaultProjectId))
        {
            await chat.InsertConversationAsync(new Conversation
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "to be cleared",
                ModelId = "qwen3",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var defaultRoot = h.Paths.ProjectRoot(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);

        (await h.Projects.DeleteAsync(SqliteDatabasePaths.DefaultProjectId)).Should().BeTrue();

        Directory.Exists(defaultRoot).Should().BeTrue("the default project is emptied, never removed");

        // Re-provisioned to a clean, usable project (fresh chat.db, no conversations).
        (await h.Projects.GetAsync(SqliteDatabasePaths.DefaultProjectId)).Should().NotBeNull();
        await using var reopened = await h.Provider.OpenChatAsync(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        (await reopened.ListConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Export_project_produces_a_non_empty_archive()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        // A file blob so the archive carries real content.
        var scope = ObjectScope.Project(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        await h.Provider.EnsureProvisionedAsync(Iss, Sub);
        await h.Objects.PutAsync(scope, "files/note.md", new MemoryStream(Encoding.UTF8.GetBytes("hello export")));

        var archive = await h.Account.ExportProjectAsync(SqliteDatabasePaths.DefaultProjectId);
        archive.ContentType.Should().Be("application/zip");

        await using var stream = await archive.OpenReadAsync(default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Export_account_produces_a_non_empty_archive()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        await h.Projects.CreateAsync(new CreateProjectRequest { Name = "Extra" });

        var archive = await h.Account.ExportAsync();
        await using var stream = await archive.OpenReadAsync(default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Delete_account_removes_the_user_dir()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        await h.Provider.EnsureProvisionedAsync(Iss, Sub);
        Directory.Exists(h.Paths.Root(Iss, Sub)).Should().BeTrue();

        await h.Account.DeleteAccountAsync();
        Directory.Exists(h.Paths.Root(Iss, Sub)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_lists_and_deletes_users()
    {
        await using var root = new TempDataRoot();
        var h = Build(root);

        await h.Provider.EnsureProvisionedAsync(Iss, "alice");
        await h.Provider.EnsureProvisionedAsync(Iss, "bob");

        var users = await h.Admin.ListUsersAsync();
        users.Select(u => u.Username).Should().Contain(["alice", "bob"]);

        var aliceKey = SqliteDatabasePaths.Key(Iss, "alice");
        (await h.Admin.GetUserAsync(aliceKey))!.Username.Should().Be("alice");
        (await h.Admin.DeleteUserAsync(aliceKey)).Should().BeTrue();
        (await h.Admin.GetUserAsync(aliceKey)).Should().BeNull();
    }
}
