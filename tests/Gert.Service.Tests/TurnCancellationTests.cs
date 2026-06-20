using FluentAssertions;
using Gert.Agent;
using Gert.Service.Chat;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The cancel registry (rest-api.md section stop generation): live-turn cancellation,
/// the tombstone covering the 202 -> worker-pickup race (with its TTL), release
/// semantics, and tenant-key isolation.
/// </summary>
public sealed class TurnCancellationTests
{
    private static readonly TurnKey Key = new("https://idp.example", "sub-123", "default", "conv-1");

    private readonly ManualClock _clock = new();
    private readonly TurnCancellation _registry;

    public TurnCancellationTests()
    {
        _registry = new TurnCancellation(Options.Create(new TurnOptions()), _clock);
    }

    [Fact]
    public void Cancel_signals_a_registered_turn_and_reports_it_live()
    {
        using var registration = _registry.Register(Key, CancellationToken.None);

        _registry.Cancel(Key).Should().BeTrue();

        registration.Token.IsCancellationRequested.Should().BeTrue();
        registration.IsUserCancelled.Should().BeTrue();
    }

    [Fact]
    public void Cancel_with_no_live_turn_reports_false_and_tombstones()
    {
        _registry.Cancel(Key).Should().BeFalse();

        // The queued job registers next - born cancelled.
        using var registration = _registry.Register(Key, CancellationToken.None);
        registration.Token.IsCancellationRequested.Should().BeTrue();
        registration.IsUserCancelled.Should().BeTrue();
    }

    [Fact]
    public void A_stale_tombstone_does_not_kill_a_future_turn()
    {
        _registry.Cancel(Key);
        _clock.Advance(new TurnOptions().MaxTurnDuration + TimeSpan.FromMinutes(1));

        using var registration = _registry.Register(Key, CancellationToken.None);
        registration.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void Release_clears_the_registration_so_a_late_cancel_is_a_noop()
    {
        var registration = _registry.Register(Key, CancellationToken.None);
        registration.Dispose();

        _registry.Cancel(Key).Should().BeFalse("the turn already ended");
        registration.IsUserCancelled.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_predecessor_does_not_release_the_successor_turn()
    {
        // The next turn of the same conversation re-registers under the same key
        // before the previous registration object is disposed.
        var first = _registry.Register(Key, CancellationToken.None);
        using var second = _registry.Register(Key, CancellationToken.None);
        first.Dispose();

        _registry.Cancel(Key).Should().BeTrue("the successor registration must still be addressable");
        second.IsUserCancelled.Should().BeTrue();
    }

    [Fact]
    public void Keys_are_tenant_scoped()
    {
        using var registration = _registry.Register(Key, CancellationToken.None);

        // Same conversation id, different tenant: must not address the turn.
        _registry.Cancel(Key with { Sub = "someone-else" }).Should().BeFalse();
        registration.IsUserCancelled.Should().BeFalse();
    }

    [Fact]
    public void The_linked_token_still_observes_the_host_side_sources()
    {
        using var host = new CancellationTokenSource();
        using var registration = _registry.Register(Key, host.Token);

        host.Cancel();

        registration.Token.IsCancellationRequested.Should().BeTrue();
        registration.IsUserCancelled.Should().BeFalse("the host fired, not the user");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) =>
            _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
