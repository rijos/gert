using System.Collections.Concurrent;
using Gert.Service.Chat;
using Microsoft.Extensions.Options;

namespace Gert.Agent;

/// <summary>
/// <see cref="ITurnCancellation"/> - a singleton map of live turns plus a
/// tombstone set for cancels that beat the worker to the job. Tombstones expire
/// after <see cref="TurnOptions.MaxTurnDuration"/> (a cancel for a job that was
/// lost to a crash must not kill a future turn of the same conversation).
/// In-process only, like the bus: the queue is in-process, so the turn a cancel
/// addresses always lives in this process.
/// </summary>
public sealed class TurnCancellation : ITurnCancellation
{
    private readonly ConcurrentDictionary<TurnKey, Registration> _live = new();
    private readonly ConcurrentDictionary<TurnKey, long> _tombstones = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _tombstoneTtl;

    public TurnCancellation(IOptions<TurnOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tombstoneTtl = options.Value.MaxTurnDuration;
    }

    /// <inheritdoc />
    public ITurnRegistration Register(TurnKey key, CancellationToken linked)
    {
        var registration = new Registration(this, key, linked);
        _live[key] = registration;

        // A cancel raced ahead of the worker: honor it if it is still fresh.
        if (_tombstones.TryRemove(key, out var stamped)
            && _clock.GetElapsedTime(stamped) <= _tombstoneTtl)
        {
            registration.CancelByUser();
        }

        return registration;
    }

    /// <inheritdoc />
    public bool Cancel(TurnKey key)
    {
        if (_live.TryGetValue(key, out var registration))
        {
            registration.CancelByUser();
            return true;
        }

        _tombstones[key] = _clock.GetTimestamp();
        return false;
    }

    private void Release(TurnKey key, Registration registration)
    {
        // Remove only OUR registration - a successor turn for the same
        // conversation may already have re-registered under this key.
        ((ICollection<KeyValuePair<TurnKey, Registration>>)_live)
            .Remove(new KeyValuePair<TurnKey, Registration>(key, registration));
        _tombstones.TryRemove(key, out _);
    }

    private sealed class Registration : ITurnRegistration
    {
        private readonly TurnCancellation _owner;
        private readonly TurnKey _key;
        private readonly CancellationTokenSource _userCts = new();
        private readonly CancellationTokenSource _combinedCts;
        private readonly Lock _gate = new();
        private bool _disposed;

        public Registration(TurnCancellation owner, TurnKey key, CancellationToken linked)
        {
            _owner = owner;
            _key = key;
            _combinedCts = CancellationTokenSource.CreateLinkedTokenSource(linked, _userCts.Token);
        }

        public CancellationToken Token => _combinedCts.Token;

        public bool IsUserCancelled => _userCts.IsCancellationRequested;

        public void CancelByUser()
        {
            // Serialised with Dispose: a cancel that loses the race against the
            // turn ending is a no-op, never an ObjectDisposedException.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _userCts.Cancel();
                }
            }
        }

        public void Dispose()
        {
            _owner.Release(_key, this);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _combinedCts.Dispose();
                _userCts.Dispose();
            }
        }
    }
}
