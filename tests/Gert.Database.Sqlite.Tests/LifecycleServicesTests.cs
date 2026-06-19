using System.Text;
using FluentAssertions;
using Gert.Database;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Rag;
using Gert.Rag.Sqlite;
using Gert.Service;
using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Projects;
using Gert.Storage;
using Gert.Storage.Local;
using Gert.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Integration tests for the four lifecycle services (Projects/Settings/Account/
/// Admin) wired over the real split SQLite adapters and a throwaway
/// <see cref="TempDataRoot"/> - proving the service-owned orchestration of the
/// user/chat/rag database providers (the DB half of a delete) + <see cref="IObjectStore"/>
/// (the artifact half), including default-emptied-not-removed and a non-empty export
/// archive. Each harness provisions the user up front, exactly as the host's
/// request-edge provisioner does.
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
        ProviderFixture.TestDatabases Provider,
        LocalObjectStore Objects,
        SqliteDatabasePaths Paths,
        IDeletionJournal Journal,
        IUserDataEraser Eraser,
        IUserContext User);

    private static async Task<Harness> BuildAsync(TempDataRoot root, string sub = Sub)
    {
        var dbs = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        var objects = ProviderFixture.ObjectsFor(root);
        var journal = ProviderFixture.JournalFor(root);
        var eraser = new UserDataEraser(dbs.Users, dbs.Rag, objects, journal);
        IUserContext user = new FixedUserContext { Sub = sub };

        // Mirror the request-edge provisioner: seed username + default project.
        await dbs.EnsureProvisionedAsync(Iss, sub);

        return new Harness(
            new ProjectService(dbs.Users, dbs.Chat, dbs.Rag, objects, user, TimeProvider.System),
            new SettingsService(dbs.Users, user),
            new AccountService(dbs.Users, dbs.Chat, objects, user, eraser),
            new AdminService(objects, dbs.Users, eraser),
            dbs,
            objects,
            paths,
            journal,
            eraser,
            user);
    }

    [Fact]
    public async Task Settings_get_defaults_then_update_merges()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var defaults = await h.Settings.GetAsync();
        defaults.ReplyLanguage.Should().BeNull();

        var updated = await h.Settings.UpdateAsync(Proof.Of(new UpdateSettingsRequest { ReplyLanguage = "nl" }));
        updated.ReplyLanguage.Should().Be("nl");

        (await h.Settings.GetAsync()).ReplyLanguage.Should().Be("nl");
    }

    [Fact]
    public async Task Project_create_list_get_round_trips_with_counts()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var created = await h.Projects.CreateAsync(Proof.Of(new CreateProjectRequest { Name = "Research" }));
        created.Name.Should().Be("Research");
        Guid.TryParseExact(created.Id, "D", out _).Should().BeTrue();

        var list = await h.Projects.ListAsync();
        list.Select(p => p.Id).Should().Contain([SqliteDatabasePaths.DefaultProjectId, created.Id]);

        var got = await h.Projects.GetAsync(created.Id);
        got!.Name.Should().Be("Research");
        got.ConversationCount.Should().Be(0);
        got.DocumentCount.Should().Be(0);
        got.MemoryCount.Should().Be(0);
    }

    [Fact]
    public async Task Project_update_merges_partial_fields()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var created = await h.Projects.CreateAsync(Proof.Of(new CreateProjectRequest { Name = "Old", Description = "keep" }));

        var updated = await h.Projects.UpdateAsync(created.Id, Proof.Of(new UpdateProjectRequest { Name = "New" }));
        updated!.Name.Should().Be("New");
        updated.Description.Should().Be("keep", "an omitted field is left unchanged");
    }

    [Fact]
    public async Task Delete_non_default_project_removes_it()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var created = await h.Projects.CreateAsync(Proof.Of(new CreateProjectRequest { Name = "Temp" }));
        // Materialise the project's databases so there is a directory to remove.
        await h.Provider.EnsureProjectAsync(Iss, Sub, created.Id);
        Directory.Exists(h.Paths.ProjectRoot(Iss, Sub, created.Id)).Should().BeTrue();

        (await h.Projects.DeleteAsync(created.Id)).Should().BeTrue();
        Directory.Exists(h.Paths.ProjectRoot(Iss, Sub, created.Id)).Should().BeFalse();
        (await h.Projects.GetAsync(created.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_default_project_is_emptied_not_removed()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

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

        // Still registered, and a clean, usable project (fresh chat.db, no conversations).
        (await h.Projects.GetAsync(SqliteDatabasePaths.DefaultProjectId)).Should().NotBeNull();
        await using var reopened = await h.Provider.OpenChatAsync(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        (await reopened.ListConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Export_project_produces_a_non_empty_archive()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        // A file blob so the archive carries real content.
        var scope = ObjectScope.Project(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
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
        var h = await BuildAsync(root);

        await h.Projects.CreateAsync(Proof.Of(new CreateProjectRequest { Name = "Extra" }));

        var archive = await h.Account.ExportAsync();
        await using var stream = await archive.OpenReadAsync(default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Export_temp_file_is_removed_once_the_archive_stream_is_closed()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var before = ExportTempFiles();

        var archive = await h.Account.ExportProjectAsync(SqliteDatabasePaths.DefaultProjectId);
        ExportTempFiles().Except(before).Should().NotBeEmpty("the archive is staged in a temp file");

        await using (var stream = await archive.OpenReadAsync(default))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
        }

        // FileOptions.DeleteOnClose: closing the (single-use) read stream deletes
        // the staging file - nothing left behind for the OS temp sweeper.
        ExportTempFiles().Except(before).Should().BeEmpty("the staging temp file is delete-on-close");
    }

    [Fact]
    public async Task Failed_export_build_deletes_its_partial_temp_file()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        var before = ExportTempFiles();

        // An already-cancelled token faults the build after the temp file exists.
        var act = () => h.Account.ExportProjectAsync(
            SqliteDatabasePaths.DefaultProjectId, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        ExportTempFiles().Except(before).Should().BeEmpty("a failed build must not strand its partial archive");
    }

    /// <summary>The current <c>gert-export-*.zip</c> staging files in the OS temp dir.</summary>
    private static IReadOnlyList<string> ExportTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "gert-export-*.zip");

    [Fact]
    public async Task Delete_account_removes_the_user_dir()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        Directory.Exists(h.Paths.Root(Iss, Sub)).Should().BeTrue();

        await h.Account.DeleteAccountAsync();
        Directory.Exists(h.Paths.Root(Iss, Sub)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_lists_and_deletes_users()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        await h.Provider.EnsureProvisionedAsync(Iss, "alice");
        await h.Provider.EnsureProvisionedAsync(Iss, "bob");

        var users = await h.Admin.ListUsersAsync();
        users.Select(u => u.Username).Should().Contain(["alice", "bob"]);

        var aliceKey = SqliteDatabasePaths.Key(Iss, "alice");
        (await h.Admin.GetUserAsync(aliceKey))!.Username.Should().Be("alice");
        (await h.Admin.DeleteUserAsync(aliceKey)).Should().BeTrue();
        (await h.Admin.GetUserAsync(aliceKey)).Should().BeNull();
    }

    [Fact]
    public async Task Admin_lists_a_folder_with_no_username_row_with_a_null_username()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        // A partially provisioned user: user.db exists (open self-migrates) but
        // the username row was never seeded. The folder must still be listed -
        // an admin may need to see and delete it - with a null username.
        await using (var repo = await h.Provider.Users.OpenAsync(Iss, "carol"))
        {
        }

        var carolKey = SqliteDatabasePaths.Key(Iss, "carol");

        var users = await h.Admin.ListUsersAsync();
        var carol = users.Should().ContainSingle(u => u.Key == carolKey).Subject;
        carol.Username.Should().BeNull();

        (await h.Admin.GetUserAsync(carolKey))!.Username.Should().BeNull();
        (await h.Admin.DeleteUserAsync(carolKey)).Should().BeTrue();
    }

    [Fact]
    public async Task Admin_get_is_null_for_a_well_shaped_but_unknown_key()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        // A well-shaped key that addresses no folder is absent, not an error.
        (await h.Admin.GetUserAsync(new string('a', 64))).Should().BeNull();
    }

    [Fact]
    public async Task Admin_delete_with_out_of_shape_key_is_false_and_touches_nothing()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        await h.Provider.EnsureProvisionedAsync(Iss, "alice");

        // A traversal / multi-segment key fails the F6 shape guard -> no deletion.
        (await h.Admin.DeleteUserAsync("../alice")).Should().BeFalse();
        Directory.GetDirectories(root.UsersDir).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delete_account_clears_the_deletion_marker()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);

        await h.Account.DeleteAccountAsync();

        var key = SqliteDatabasePaths.Key(Iss, Sub);
        (await h.Journal.IsPendingAsync(key)).Should().BeFalse("a completed erase clears its journal mark");
        (await h.Journal.ListPendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_interrupted_deletion_is_completed_by_replaying_the_eraser()
    {
        await using var root = new TempDataRoot();
        var h = await BuildAsync(root);
        var key = SqliteDatabasePaths.Key(Iss, Sub);

        // Simulate a crash mid-delete: the intent mark is owed and residue (a blob) remains.
        await h.Journal.MarkPendingAsync(key);
        var scope = ObjectScope.Project(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        await h.Objects.PutAsync(scope, "files/leftover", new MemoryStream(Encoding.UTF8.GetBytes("residue")));
        (await h.Journal.IsPendingAsync(key)).Should().BeTrue();

        // Recovery (the startup sweep / the provisioner gate) replays the idempotent erase.
        await h.Eraser.EraseAsync(key);

        (await h.Journal.IsPendingAsync(key)).Should().BeFalse("recovery clears the mark once everything is gone");
        Directory.Exists(h.Paths.Root(Iss, Sub)).Should().BeFalse("the residue is erased to completion");
    }

    [Fact]
    public async Task Delete_account_erases_rag_db_under_a_separate_rag_root()
    {
        // The DB + object store + journal share one root; the RAG engine gets its own.
        await using var sharedRoot = new TempDataRoot();
        await using var ragRoot = new TempDataRoot();

        var sharedOpt = Options.Create(ProviderFixture.OptionsFor(sharedRoot));
        var ragParams = Options.Create(new SqliteRagParameters { DataRoot = ragRoot.Path });

        var dbPaths = new SqliteDatabasePaths(sharedOpt, Options.Create(new SqliteDatabaseParameters()));
        var ragPaths = new SqliteRagPaths(sharedOpt, ragParams);
        IUserDatabaseProvider users =
            new SqliteUserDatabaseProvider(dbPaths, new SqliteConnectionFactory(), TimeProvider.System);
        IRagIndexProvider rag = new SqliteRagIndexProvider(ragPaths, new SqliteRagConnectionFactory(ragParams));
        var objects = ProviderFixture.ObjectsFor(sharedRoot);
        var journal = ProviderFixture.JournalFor(sharedRoot);
        var eraser = new UserDataEraser(users, rag, objects, journal);

        // Provision the user + materialise a project's rag.db under the SEPARATE rag root.
        await using (var repo = await users.OpenAsync(Iss, Sub))
        {
            await repo.SetUsernameAsync(Sub);
        }

        await (await rag.OpenAsync(Iss, Sub, SqliteDatabasePaths.DefaultProjectId)).DisposeAsync();
        var ragDb = ragPaths.RagDb(Iss, Sub, SqliteDatabasePaths.DefaultProjectId);
        File.Exists(ragDb).Should().BeTrue("rag.db materialises under its own root");
        ragDb.Should().StartWith(ragRoot.Path).And.NotContain(sharedRoot.Path);

        // Only the RAG-engine delete reaches a rag.db that is NOT in the object store's tree;
        // dropping that orchestration call would leave this file behind.
        (await eraser.EraseAsync(SqliteDatabasePaths.Key(Iss, Sub))).Should().BeTrue();

        File.Exists(ragDb).Should().BeFalse("the RAG-engine delete reached the separate rag root");
        (await journal.IsPendingAsync(SqliteDatabasePaths.Key(Iss, Sub))).Should().BeFalse();
    }
}
