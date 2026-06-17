using FluentAssertions;
using Gert.Storage;
using Gert.Storage.Local;
using Gert.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// <see cref="LocalDeletionJournal"/> - the durable owed-deletion marks that drive
/// crash-consistent erasure. Markers live beside <c>users/</c> (so a user-tree wipe never
/// takes them) and the key is F6-shape-validated before any path is formed.
/// </summary>
public class LocalDeletionJournalTests
{
    private static readonly string KeyA = new('a', 64);
    private static readonly string KeyB = new('b', 64);

    private static LocalDeletionJournal JournalFor(TempDataRoot root) =>
        new(Options.Create(new StorageOptions { DataRoot = root.Path }));

    [Fact]
    public async Task Mark_then_clear_round_trips_and_lists_pending()
    {
        await using var root = new TempDataRoot();
        var journal = JournalFor(root);

        (await journal.IsPendingAsync(KeyA)).Should().BeFalse();
        (await journal.ListPendingAsync()).Should().BeEmpty();

        await journal.MarkPendingAsync(KeyA);
        await journal.MarkPendingAsync(KeyB);
        await journal.MarkPendingAsync(KeyA); // idempotent re-mark

        (await journal.IsPendingAsync(KeyA)).Should().BeTrue();
        (await journal.ListPendingAsync()).Should().BeEquivalentTo(new[] { KeyA, KeyB });

        await journal.ClearAsync(KeyA);
        await journal.ClearAsync(KeyA); // idempotent re-clear

        (await journal.IsPendingAsync(KeyA)).Should().BeFalse();
        (await journal.ListPendingAsync()).Should().Equal(KeyB);
    }

    [Fact]
    public async Task The_marker_lives_outside_the_users_tree()
    {
        await using var root = new TempDataRoot();
        var journal = JournalFor(root);

        await journal.MarkPendingAsync(KeyA);

        // A whole-account blob wipe removes users/{key}; the mark must survive it so recovery
        // still knows a delete is owed.
        var store = new LocalObjectStore(Options.Create(new StorageOptions { DataRoot = root.Path }));
        await store.DeleteScopeAsync(ObjectScope.FromUserKey(KeyA));

        (await journal.IsPendingAsync(KeyA)).Should().BeTrue("the journal is a sibling of users/, not under it");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("not-hex")]
    [InlineData("AAAA")]
    public async Task Out_of_shape_keys_are_rejected_before_any_path_is_formed(string badKey)
    {
        await using var root = new TempDataRoot();
        var journal = JournalFor(root);

        await ((Func<Task>)(() => journal.MarkPendingAsync(badKey))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => journal.IsPendingAsync(badKey))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => journal.ClearAsync(badKey))).Should().ThrowAsync<ArgumentException>();
    }
}
