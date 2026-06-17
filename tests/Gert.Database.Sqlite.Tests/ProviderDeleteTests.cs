using FluentAssertions;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// The database providers' destruction half of a delete (principle #5): each provider
/// drops the engine's pooled handles and removes only its own database files - the
/// chat/rag providers one project's <c>chat.db</c>/<c>rag.db</c>, the user provider the
/// whole account's files. The artifact half (blobs) is the <see cref="Gert.Storage.IObjectStore"/>'s
/// and is exercised by <see cref="LifecycleServicesTests"/>; here we assert the engine
/// half stands alone with no cross-layer handle releaser, and that a re-open
/// self-provisions a clean database.
/// </summary>
public class ProviderDeleteTests
{
    private const string Sub = "delete-sub";

    private static string Iss => ProviderFixture.ExpectedIssuer;

    [Fact]
    public async Task Chat_delete_project_removes_chat_db_and_reopens_clean()
    {
        await using var root = new TempDataRoot();
        var dbs = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        await dbs.EnsureProvisionedAsync(Iss, Sub);

        var pid = Guid.NewGuid().ToString("D");
        await dbs.EnsureProjectAsync(Iss, Sub, pid);
        File.Exists(paths.ChatDb(Iss, Sub, pid)).Should().BeTrue();

        (await dbs.Chat.DeleteProjectAsync(Iss, Sub, pid)).Should().BeTrue();
        File.Exists(paths.ChatDb(Iss, Sub, pid)).Should().BeFalse();

        // Idempotent: a second delete reports nothing existed.
        (await dbs.Chat.DeleteProjectAsync(Iss, Sub, pid)).Should().BeFalse();

        // A re-open self-provisions a fresh, empty database (no stale rows resurface).
        await using var reopened = await dbs.OpenChatAsync(Iss, Sub, pid);
        (await reopened.ListConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Rag_delete_project_removes_rag_db()
    {
        await using var root = new TempDataRoot();
        var dbs = ProviderFixture.ProviderFor(root);
        var ragPaths = ProviderFixture.RagPathsFor(root);
        await dbs.EnsureProvisionedAsync(Iss, Sub);

        var pid = Guid.NewGuid().ToString("D");
        await dbs.EnsureProjectAsync(Iss, Sub, pid);
        File.Exists(ragPaths.RagDb(Iss, Sub, pid)).Should().BeTrue();

        (await dbs.Rag.DeleteProjectAsync(Iss, Sub, pid)).Should().BeTrue();
        File.Exists(ragPaths.RagDb(Iss, Sub, pid)).Should().BeFalse();
    }

    [Fact]
    public async Task User_delete_takes_user_db_and_every_project_db_across_both_engines()
    {
        await using var root = new TempDataRoot();
        var dbs = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        var ragPaths = ProviderFixture.RagPathsFor(root);
        await dbs.EnsureProvisionedAsync(Iss, Sub);

        var pid = Guid.NewGuid().ToString("D");
        await dbs.EnsureProjectAsync(Iss, Sub, pid);
        File.Exists(paths.UserDb(Iss, Sub)).Should().BeTrue();
        File.Exists(paths.ChatDb(Iss, Sub, pid)).Should().BeTrue();
        File.Exists(ragPaths.RagDb(Iss, Sub, pid)).Should().BeTrue();

        // The database engine owns user.db + chat.db; the RAG engine owns rag.db. The
        // service orchestrates both - here we drive both providers directly.
        (await dbs.Users.DeleteUserAsync(Iss, Sub)).Should().BeTrue();
        (await dbs.Rag.DeleteUserAsync(Iss, Sub)).Should().BeTrue();

        File.Exists(paths.UserDb(Iss, Sub)).Should().BeFalse();
        File.Exists(paths.ChatDb(Iss, Sub, pid)).Should().BeFalse("the database engine takes user.db + every chat.db");
        File.Exists(ragPaths.RagDb(Iss, Sub, pid)).Should().BeFalse("the RAG engine takes every rag.db");
    }

    [Fact]
    public async Task User_delete_by_key_removes_user_and_rag_dbs()
    {
        await using var root = new TempDataRoot();
        var dbs = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        var ragPaths = ProviderFixture.RagPathsFor(root);
        await dbs.EnsureProvisionedAsync(Iss, Sub);

        var pid = Guid.NewGuid().ToString("D");
        await dbs.EnsureProjectAsync(Iss, Sub, pid);

        var key = SqliteDatabasePaths.Key(Iss, Sub);
        (await dbs.Users.DeleteUserByKeyAsync(key)).Should().BeTrue();
        (await dbs.Rag.DeleteUserByKeyAsync(key)).Should().BeTrue();
        File.Exists(paths.UserDb(Iss, Sub)).Should().BeFalse();
        File.Exists(ragPaths.RagDb(Iss, Sub, pid)).Should().BeFalse();

        // Idempotent: deleting a missing user reports nothing existed.
        (await dbs.Users.DeleteUserByKeyAsync(key)).Should().BeFalse();
        (await dbs.Rag.DeleteUserByKeyAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task User_delete_by_key_rejects_an_out_of_shape_key()
    {
        await using var root = new TempDataRoot();
        var dbs = ProviderFixture.ProviderFor(root);

        // A traversal / multi-segment key fails the F6 shape guard before any path forms.
        var act = () => dbs.Users.DeleteUserByKeyAsync("../alice");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
