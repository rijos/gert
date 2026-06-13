using FluentAssertions;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Round-trips the slimmed <see cref="Gert.Storage.ObjectStoreUserStore"/> (over the
/// local <see cref="Gert.Storage.LocalObjectStore"/> backend) against a throwaway
/// <see cref="TempDataRoot"/> - the blob lifecycle only: delete/empty a project
/// (incl. its databases), account delete, the admin footprint scan, and the F6
/// key-shape guard. Settings, the project registry, and usernames now live in
/// <c>user.db</c> (covered by <see cref="SqliteUserRepositoryTests"/> and
/// <c>LifecycleServicesTests</c>).
/// </summary>
public class ObjectStoreUserStoreTests
{
    private const string Sub = "store-sub";

    private static string Iss => ProviderFixture.ExpectedIssuer;

    private static Gert.Storage.ObjectStoreUserStore StoreFor(TempDataRoot root) =>
        ProviderFixture.StoreFor(root);

    private static ProviderFixture.TestDatabases ProviderFor(TempDataRoot root) =>
        ProviderFixture.ProviderFor(root);

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

        var projectRoot = paths.ProjectRoot(Iss, Sub, "default");
        Directory.Exists(projectRoot).Should().BeTrue();
        File.Exists(paths.ChatDb(Iss, Sub, "default")).Should().BeTrue();

        await store.EmptyProjectAsync(Iss, Sub, "default");

        Directory.Exists(projectRoot).Should().BeTrue("the default project dir is kept");
        Directory.GetFileSystemEntries(projectRoot).Should().BeEmpty("its contents (incl. the databases) are cleared");
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
    public async Task Footprint_lists_and_gets_provisioned_users()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, "alice");
        await provider.EnsureProvisionedAsync(Iss, "bob");
        var store = StoreFor(root);

        var footprints = await store.ListUserFootprintsAsync();
        footprints.Should().HaveCount(2);

        var aliceKey = SqliteDatabasePaths.Key(Iss, "alice");
        var alice = await store.GetUserFootprintAsync(aliceKey);
        alice!.Key.Should().Be(aliceKey);

        (await store.DeleteUserByKeyAsync(aliceKey)).Should().BeTrue();
        (await store.GetUserFootprintAsync(aliceKey)).Should().BeNull();
        (await store.ListUserFootprintsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Get_footprint_is_null_for_unknown_user()
    {
        await using var root = new TempDataRoot();
        var store = StoreFor(root);

        // A well-shaped key that addresses no folder is absent, not an error.
        (await store.GetUserFootprintAsync(new string('a', 64))).Should().BeNull();
    }

    [Fact]
    public async Task Admin_delete_by_key_with_traversal_segment_does_not_escape()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, "alice");
        var store = StoreFor(root);

        // A multi-segment / traversal key fails the F6 shape guard -> no deletion,
        // the tree is untouched.
        (await store.DeleteUserByKeyAsync("../alice")).Should().BeFalse();
        Directory.GetDirectories(root.UsersDir).Should().NotBeEmpty();
    }
}
