using System.Collections.Concurrent;
using FluentAssertions;
using Gert.Agent;
using Gert.Service.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The turn launcher, driven directly - no TestServer: a real <see cref="TurnLauncher"/> over a
/// gating fake runner. Per-conversation serialization is the gate index's job (a second turn is 409'd
/// at plan time), so the launcher only bounds GLOBAL concurrency: turns run up to the cap, the rest
/// wait; a defect or shutdown is contained.
/// </summary>
public sealed class TurnLauncherTests : IAsyncDisposable
{
    private const string Iss = "https://id.test.local";
    private const string Sub = "launcher-sub";
    private const string Pid = "default";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly GatedRunner _runner = new();
    private readonly ServiceProvider _services;

    public TurnLauncherTests()
    {
        var services = new ServiceCollection();
        // The launcher's per-job scope shape: a scoped context it seeds first, and
        // the runner it resolves afterwards (a singleton fake here).
        services.AddScoped<DetachedUserContext>();
        services.AddSingleton<ITurnRunner>(_runner);
        _services = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private TurnLauncher LauncherFor(int cap) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new TurnOptions { MaxConcurrentTurns = cap }),
        NullLogger<TurnLauncher>.Instance);

    [Fact]
    public async Task Turns_run_concurrently_up_to_the_cap()
    {
        var launcher = LauncherFor(cap: 2);
        var a = NewJob();
        var b = NewJob();

        try
        {
            await launcher.EnqueueAsync(a);
            await launcher.EnqueueAsync(b);

            // Both runners start while NEITHER gate is released: cap=2 allows two concurrent runs -
            // the old global serial worker could never do this.
            await Task.WhenAll(_runner.StartedAsync(a), _runner.StartedAsync(b)).WaitAsync(Timeout);

            _runner.Release(a);
            _runner.Release(b);
        }
        finally
        {
            await StopAsync(launcher);
        }
    }

    [Fact]
    public async Task The_concurrency_cap_serializes_turns_beyond_it()
    {
        var launcher = LauncherFor(cap: 1);
        var first = NewJob();
        var second = NewJob();

        try
        {
            await launcher.EnqueueAsync(first);
            await _runner.StartedAsync(first).WaitAsync(Timeout); // first holds the only slot

            await launcher.EnqueueAsync(second);
            // Give a wrongly-parallel run a beat to manifest, then assert the second turn is still
            // waiting behind the single slot.
            await Task.Delay(50);
            _runner.HasStarted(second).Should().BeFalse("the cap-1 gate serializes the second turn");

            _runner.Release(first);
            await _runner.StartedAsync(second).WaitAsync(Timeout); // slot freed -> second runs
            _runner.Release(second);
            await _runner.FinishedAsync(second).WaitAsync(Timeout);

            _runner.Events.Should().Equal(
                $"start:{JobId(first)}", $"end:{JobId(first)}",
                $"start:{JobId(second)}", $"end:{JobId(second)}");
        }
        finally
        {
            await StopAsync(launcher);
        }
    }

    [Fact]
    public async Task A_runner_fault_does_not_kill_the_launcher()
    {
        var launcher = LauncherFor(cap: 1);
        var faulty = NewJob();
        var next = NewJob();
        _runner.ThrowFor(faulty);

        try
        {
            await launcher.EnqueueAsync(faulty);
            await _runner.StartedAsync(faulty).WaitAsync(Timeout); // starts, throws, frees the slot

            await launcher.EnqueueAsync(next);

            // The defect is swallowed by the per-job guard; the launcher lives on.
            await _runner.StartedAsync(next).WaitAsync(Timeout);
            _runner.Release(next);
        }
        finally
        {
            await StopAsync(launcher);
        }
    }

    [Fact]
    public async Task Shutdown_cancels_inflight_runners()
    {
        var launcher = LauncherFor(cap: 1);
        var job = NewJob();

        await launcher.EnqueueAsync(job);
        await _runner.StartedAsync(job).WaitAsync(Timeout);

        // Never release the gate: stop must cancel the in-flight runner with the shutdown token.
        await StopAsync(launcher);

        _runner.ObservedCancellation(job).Should().BeTrue("the in-flight runner gets the stopping token");
    }

    private static async Task StopAsync(TurnLauncher launcher)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await launcher.StopAsync(cts.Token);
    }

    private static string JobId(TurnJob job) => job.AssistantMessageId;

    private static TurnJob NewJob() => new()
    {
        Iss = Iss,
        Sub = Sub,
        Username = "tester",
        AllowedToolIds = new HashSet<string>(StringComparer.Ordinal),
        Pid = Pid,
        ConversationId = Guid.NewGuid().ToString("D"),
        UserMessageId = Guid.NewGuid().ToString("D"),
        AssistantMessageId = Guid.NewGuid().ToString("D"),
        AssistantSeq = 2,
        PlannedAt = DateTimeOffset.UtcNow,
        ModelId = "default",
        History = [],
    };

    /// <summary>
    /// A blocking <see cref="ITurnRunner"/>: records start/end per job, then parks on a per-job gate
    /// until the test releases it (or the stopping token cancels). <c>Gert.Testing</c>'s
    /// <c>FakeChatModel</c> is fixture-driven and cannot block - this fake exists precisely to hold a
    /// slot open.
    /// </summary>
    private sealed class GatedRunner : ITurnRunner
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<string, bool> _cancelled = new();
        private readonly ConcurrentDictionary<string, bool> _throwFor = new();
        private readonly ConcurrentQueue<string> _events = new();

        public IReadOnlyCollection<string> Events => _events;

        public Task StartedAsync(TurnJob job) => For(_started, JobId(job)).Task;

        public Task FinishedAsync(TurnJob job) => For(_finished, JobId(job)).Task;

        public bool HasStarted(TurnJob job) => For(_started, JobId(job)).Task.IsCompleted;

        public void Release(TurnJob job) => For(_gates, JobId(job)).TrySetResult();

        public void ThrowFor(TurnJob job) => _throwFor[JobId(job)] = true;

        public bool ObservedCancellation(TurnJob job) => _cancelled.ContainsKey(JobId(job));

        public async Task RunAsync(TurnJob job, CancellationToken cancellationToken = default)
        {
            var id = JobId(job);
            _events.Enqueue($"start:{id}");
            For(_started, id).TrySetResult();

            if (_throwFor.ContainsKey(id))
            {
                throw new InvalidOperationException("runner defect (test)");
            }

            try
            {
                await For(_gates, id).Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled[id] = true;
                throw;
            }

            _events.Enqueue($"end:{id}");
            For(_finished, id).TrySetResult();
        }

        private static TaskCompletionSource For(
            ConcurrentDictionary<string, TaskCompletionSource> map,
            string id) =>
            map.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
