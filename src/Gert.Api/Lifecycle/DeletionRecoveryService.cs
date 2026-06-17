using Gert.Service.Account;
using Gert.Storage;

namespace Gert.Api.Lifecycle;

/// <summary>
/// The forward-recovery half of the deletion saga (storage-and-data.md section deletion;
/// decisions section deletion crash-consistency). On startup it reads the owed marks from the
/// <see cref="IDeletionJournal"/> and replays the idempotent <see cref="IUserDataEraser"/> for
/// each, so an account deletion that a previous process left interrupted converges to
/// fully-deleted - no operator retry, no orphaned residue. A failure for one user is logged
/// and its mark left in place (the next start retries); it never blocks host startup.
/// </summary>
public sealed class DeletionRecoveryService : IHostedService
{
    private readonly IDeletionJournal _journal;
    private readonly IUserDataEraser _eraser;
    private readonly ILogger<DeletionRecoveryService> _logger;

    public DeletionRecoveryService(
        IDeletionJournal journal,
        IUserDataEraser eraser,
        ILogger<DeletionRecoveryService> logger)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _eraser = eraser ?? throw new ArgumentNullException(nameof(eraser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pending;
        try
        {
            pending = await _journal.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Recovery is best-effort; a journal read failure must never block startup.
            _logger.LogError(ex, "Could not read the deletion journal at startup; skipping deletion recovery.");
            return;
        }

        if (pending.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Completing {Count} interrupted account deletion(s) left by a previous run.", pending.Count);

        foreach (var key in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _eraser.EraseAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Leave the mark in place so a later start retries; keep going for the rest.
                _logger.LogError(ex, "Failed to complete a pending account deletion; it stays journalled for retry.");
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
