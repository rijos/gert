namespace Gert.Service.Chat;

/// <summary>
/// Process-wide registry of in-flight turns, addressable for user-initiated
/// cancellation (rest-api.md section stop generation). The runner registers at turn
/// start and runs under <see cref="ITurnRegistration.Token"/>; the cancel
/// endpoint / WS handler calls <see cref="Cancel"/>. A cancel that lands while
/// the job is still queued (the 202 -> worker-pickup race) leaves a short-lived
/// tombstone that pre-cancels the eventual <see cref="Register"/>.
/// </summary>
public interface ITurnCancellation
{
    /// <summary>
    /// Register a turn and get the token it must run under: <paramref name="linked"/>
    /// (host shutdown + wall-clock cap) combined with the user-cancel source. If a
    /// fresh tombstone exists for <paramref name="key"/>, the registration is born
    /// cancelled. Dispose the registration when the turn ends (releases the key).
    /// </summary>
    ITurnRegistration Register(TurnKey key, CancellationToken linked);

    /// <summary>
    /// Request cancellation of the turn for <paramref name="key"/>. Returns
    /// <c>true</c> when a live turn was signalled; <c>false</c> when none was
    /// registered (a tombstone is recorded instead - idempotent no-op otherwise).
    /// </summary>
    bool Cancel(TurnKey key);
}

/// <summary>One registered turn - disposed by the runner when the turn ends.</summary>
public interface ITurnRegistration : IDisposable
{
    /// <summary>The token the turn runs under (host + timeout + user cancel).</summary>
    CancellationToken Token { get; }

    /// <summary>True when the cancellation came from the user (not shutdown/timeout).</summary>
    bool IsUserCancelled { get; }
}
