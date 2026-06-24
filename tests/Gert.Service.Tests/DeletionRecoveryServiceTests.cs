using FluentAssertions;
using Gert.Service.Account;
using Gert.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// <see cref="DeletionRecoveryService"/> - the forward-recovery half of the deletion
/// saga. On startup it replays the idempotent eraser for every owed mark, swallows a
/// per-key failure (leaving that mark for a later retry), and is a no-op when nothing
/// is owed - never blocking host startup.
/// </summary>
public sealed class DeletionRecoveryServiceTests
{
    private readonly IDeletionJournal _journal = Substitute.For<IDeletionJournal>();
    private readonly IUserDataEraser _eraser = Substitute.For<IUserDataEraser>();

    private DeletionRecoveryService NewService() =>
        new(_journal, _eraser, NullLogger<DeletionRecoveryService>.Instance);

    [Fact]
    public async Task Each_pending_key_is_erased()
    {
        _journal.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)["key-a", "key-b", "key-c"]);

        await NewService().StartAsync(CancellationToken.None);

        await _eraser.Received(1).EraseAsync("key-a", Arg.Any<CancellationToken>());
        await _eraser.Received(1).EraseAsync("key-b", Arg.Any<CancellationToken>());
        await _eraser.Received(1).EraseAsync("key-c", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_eraser_failure_for_one_key_does_not_abort_the_others()
    {
        _journal.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)["key-a", "key-b", "key-c"]);
        _eraser.EraseAsync("key-b", Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new IOException("volume gone"));

        var act = () => NewService().StartAsync(CancellationToken.None);

        // The failing key is logged and left journalled; the rest still run, and
        // startup never throws.
        await act.Should().NotThrowAsync();
        await _eraser.Received(1).EraseAsync("key-a", Arg.Any<CancellationToken>());
        await _eraser.Received(1).EraseAsync("key-c", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_pending_list_is_a_no_op()
    {
        _journal.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)[]);

        await NewService().StartAsync(CancellationToken.None);

        await _eraser.DidNotReceive().EraseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_journal_read_failure_never_blocks_startup()
    {
        _journal.ListPendingAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new IOException("journal unreadable"));

        var act = () => NewService().StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _eraser.DidNotReceive().EraseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
