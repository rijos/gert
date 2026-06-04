using FluentAssertions;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Round-trips <see cref="Gert.Storage.ObjectStoreUserStore"/> (over the local
/// <see cref="Gert.Storage.LocalObjectStore"/> backend) against a throwaway
/// <see cref="TempDataRoot"/> — settings + project meta read/merge/write, the
/// default-emptied-not-removed contract, account delete, admin scan/delete, and
/// the F6 key-shape guard.
/// </summary>
public class ObjectStoreUserStoreTests
{
    private const string Sub = "store-sub";

    private static string Iss => ProviderFixture.ExpectedIssuer;

    private static Gert.Storage.ObjectStoreUserStore StoreFor(TempDataRoot root) =>
        ProviderFixture.StoreFor(root);

    private static SqliteDatabaseProvider ProviderFor(TempDataRoot root) =>
        ProviderFixture.ProviderFor(root);

    [Fact]
    public async Task Settings_default_then_round_trips()
    {
        await using var root = new TempDataRoot();
        await ProviderFor(root).EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);

        var initial = await store.GetSettingsAsync(Iss, Sub);
        initial.Should().NotBeNull();

        var saved = initial with { ReplyLanguage = "nl", Theme = Theme.Dark };
        await store.SaveSettingsAsync(Iss, Sub, saved);

        var reloaded = await store.GetSettingsAsync(Iss, Sub);
        reloaded.ReplyLanguage.Should().Be("nl");
        reloaded.Theme.Should().Be(Theme.Dark);
    }

    [Fact]
    public async Task Settings_default_when_absent()
    {
        await using var root = new TempDataRoot();
        var store = StoreFor(root);

        // Nothing provisioned — defaults, not a throw.
        var settings = await store.GetSettingsAsync(Iss, Sub);
        settings.Should().BeEquivalentTo(new UserSettings());
    }

    [Fact]
    public async Task Project_save_get_list_round_trips()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);

        var pid = Guid.NewGuid().ToString("D");
        await provider.EnsureProjectAsync(Iss, Sub, pid);

        var meta = new ProjectMeta
        {
            Id = pid,
            Name = "Research",
            Description = "notes",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveProjectAsync(Iss, Sub, meta);

        var fetched = await store.GetProjectAsync(Iss, Sub, pid);
        fetched!.Name.Should().Be("Research");
        fetched.Description.Should().Be("notes");

        var list = await store.ListProjectsAsync(Iss, Sub);
        list.Select(p => p.Id).Should().Contain([SqliteDatabasePaths.DefaultProjectId, pid]);
    }

    [Fact]
    public async Task Delete_project_rm_rf_removes_the_dir()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        var pid = Guid.NewGuid().ToString("D");
        await provider.EnsureProjectAsync(Iss, Sub, pid);
        Directory.Exists(paths.ProjectRoot(Iss, Sub, pid)).Should().BeTrue();

        (await store.DeleteProjectAsync(Iss, Sub, pid)).Should().BeTrue();
        Directory.Exists(paths.ProjectRoot(Iss, Sub, pid)).Should().BeFalse();

        // Idempotent: deleting a missing project returns false.
        (await store.DeleteProjectAsync(Iss, Sub, pid)).Should().BeFalse();
    }

    [Fact]
    public async Task Empty_project_clears_contents_keeps_dir()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        var projectRoot = paths.ProjectRoot(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        Directory.Exists(projectRoot).Should().BeTrue();
        File.Exists(paths.ChatDb(Iss, Sub, SqliteDatabasePaths.DefaultProjectId)).Should().BeTrue();

        await store.EmptyProjectAsync(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);

        Directory.Exists(projectRoot).Should().BeTrue("the default project dir is kept");
        Directory.GetFileSystemEntries(projectRoot).Should().BeEmpty("its contents are cleared");
    }

    [Fact]
    public async Task Delete_user_removes_the_user_dir()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        Directory.Exists(paths.Root(Iss, Sub)).Should().BeTrue();

        (await store.DeleteUserAsync(Iss, Sub)).Should().BeTrue();
        Directory.Exists(paths.Root(Iss, Sub)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_list_and_get_and_delete_by_key()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, "alice");
        await provider.EnsureProvisionedAsync(Iss, "bob");
        var store = StoreFor(root);

        var users = await store.ListUsersAsync();
        users.Should().HaveCount(2);

        var aliceKey = SqliteDatabasePaths.Key(Iss, "alice");
        var alice = await store.GetUserAsync(aliceKey);
        alice!.Key.Should().Be(aliceKey);
        alice.Username.Should().Be("alice");

        (await store.DeleteUserByKeyAsync(aliceKey)).Should().BeTrue();
        (await store.GetUserAsync(aliceKey)).Should().BeNull();
        (await store.ListUsersAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Settings_default_when_file_empty()
    {
        await using var root = new TempDataRoot();
        await ProviderFor(root).EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        // A 0-byte settings.json (e.g. a write killed before its atomic rename) must
        // fall back to defaults, not throw a JsonException.
        await File.WriteAllTextAsync(paths.SettingsFile(Iss, Sub), string.Empty);

        var settings = await store.GetSettingsAsync(Iss, Sub);
        settings.Should().BeEquivalentTo(new UserSettings());
    }

    [Fact]
    public async Task List_projects_skips_corrupt_meta()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        // A second project whose meta.json holds non-JSON garbage is skipped, not
        // fatal — the valid default project still lists.
        var pid = Guid.NewGuid().ToString("D");
        await provider.EnsureProjectAsync(Iss, Sub, pid);
        await File.WriteAllTextAsync(paths.ProjectMeta(Iss, Sub, pid), "{ not valid json");

        var ids = (await store.ListProjectsAsync(Iss, Sub)).Select(p => p.Id).ToList();
        ids.Should().Contain(SqliteDatabasePaths.DefaultProjectId);
        ids.Should().NotContain(pid);
    }

    [Fact]
    public async Task Admin_list_skips_user_with_empty_meta()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, "alice");
        await provider.EnsureProvisionedAsync(Iss, "bob");
        var store = StoreFor(root);
        var paths = ProviderFixture.PathsFor(root);

        // Reproduces the original 500: bob's meta.json truncated to 0 bytes must be
        // skipped so the admin scan still returns the healthy users.
        await File.WriteAllTextAsync(Path.Combine(paths.Root(Iss, "bob"), "meta.json"), string.Empty);

        var users = await store.ListUsersAsync();
        users.Should().ContainSingle(u => u.Username == "alice");
    }

    [Fact]
    public async Task Admin_delete_by_key_with_traversal_segment_does_not_escape()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, "alice");
        var store = StoreFor(root);

        // A multi-segment / traversal key fails the F6 shape guard → no deletion,
        // the tree is untouched.
        (await store.DeleteUserByKeyAsync("../alice")).Should().BeFalse();
        Directory.GetDirectories(root.UsersDir).Should().NotBeEmpty();
    }
}
