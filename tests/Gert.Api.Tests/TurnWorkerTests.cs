using System.Collections.Concurrent;
using FluentAssertions;
using Gert.Api.Chat;
using Gert.Service.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The keyed-lane worker (decisions section 11), driven directly - no TestServer: a
/// sharded <see cref="ChannelTurnQueue"/>, a real <see cref="TurnWorker"/>, and
/// a gating fake runner. <see cref="ChannelTurnQueue.ShardFor"/> picks
/// conversation ids deterministically (no hash luck): different lanes provably
/// overlap, one lane is strict FIFO, a defect or shutdown is contained per lane.
/// </summary>
public sealed class TurnWorkerTests : IAsyncDisposable
{
    private const string Iss = "https://id.test.local";
    private const string Sub = "worker-sub";
    private const string Pid = "default";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly GatedRunner _runner = new();
    private readonly ServiceProvider _services;

    public TurnWorkerTests()
    {
        var services = new ServiceCollection();
        // The worker's per-job scope shape: a scoped context it seeds first, and
        // the runner it resolves afterwards (a singleton fake here).
        services.AddScoped<DetachedUserContext>();
        services.AddSingleton<ITurnRunner>(_runner);
        _services = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private TurnWorker WorkerFor(ChannelTurnQueue queue) => new(
        queue,
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<TurnWorker>.Instance);

    private static ChannelTurnQueue QueueFor(int lanes) =>
        new(Options.Create(new TurnOptions { MaxConcurrentTurns = lanes }));

    [Fact]
    public async Task Turns_in_different_conversations_run_concurrently()
    {
        var queue = QueueFor(lanes: 2);
        var convA = ConversationOnShard(shard: 0, queue.ShardCount);
        var convB = ConversationOnShard(shard: 1, queue.ShardCount);
        var jobA = NewJob(convA);
        var jobB = NewJob(convB);

        var worker = WorkerFor(queue);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(jobA);
            await queue.EnqueueAsync(jobB);

            // Both runners started while NEITHER gate is released: the two lanes
            // overlap - the old global serial worker could never do this.
            await Task.WhenAll(_runner.StartedAsync(jobA), _runner.StartedAsync(jobB)).WaitAsync(Timeout);

            _runner.Release(jobA);
            _runner.Release(jobB);
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Turns_on_one_lane_never_overlap()
    {
        var queue = QueueFor(lanes: 2);
        // The realistic same-lane case: two turns for ONE conversation.
        var conv = ConversationOnShard(shard: 0, queue.ShardCount);
        var first = NewJob(conv);
        var second = NewJob(conv);

        var worker = WorkerFor(queue);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(first);
            await queue.EnqueueAsync(second);

            await _runner.StartedAsync(first).WaitAsync(Timeout);
            // Give a wrongly-parallel lane a beat to manifest, then assert the
            // second job is still waiting behind the first's gate.
            await Task.Delay(50);
            _runner.HasStarted(second).Should().BeFalse("one lane is strict FIFO");

            _runner.Release(first);
            await _runner.StartedAsync(second).WaitAsync(Timeout);
            _runner.Release(second);
            await _runner.FinishedAsync(second).WaitAsync(Timeout);

            _runner.Events.Should().Equal(
                $"start:{JobId(first)}", $"end:{JobId(first)}",
                $"start:{JobId(second)}", $"end:{JobId(second)}");
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task A_runner_fault_does_not_kill_its_lane()
    {
        var queue = QueueFor(lanes: 2);
        var conv = ConversationOnShard(shard: 0, queue.ShardCount);
        var faulty = NewJob(conv);
        var next = NewJob(conv);
        _runner.ThrowFor(faulty);

        var worker = WorkerFor(queue);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(faulty);
            await queue.EnqueueAsync(next);

            // The defect is swallowed by the per-job guard; the lane lives on.
            await _runner.StartedAsync(next).WaitAsync(Timeout);
            _runner.Release(next);
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Shutdown_cancels_inflight_lanes()
    {
        var queue = QueueFor(lanes: 2);
        var job = NewJob(ConversationOnShard(shard: 0, queue.ShardCount));

        var worker = WorkerFor(queue);
        await worker.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(job);
        await _runner.StartedAsync(job).WaitAsync(Timeout);

        // Never release the gate: stop must cancel the in-flight runner and
        // every lane must observe the shutdown so ExecuteAsync completes.
        await StopAsync(worker);

        _runner.ObservedCancellation(job).Should().BeTrue("the in-flight runner gets the stopping token");
        worker.ExecuteTask.Should().NotBeNull();
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("every lane observes shutdown cleanly");
    }

    // ---- harness -------------------------------------------------------------

    private static async Task StopAsync(TurnWorker worker)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await worker.StopAsync(cts.Token);
    }

    private static string JobId(TurnJob job) => job.AssistantMessageId;

    /// <summary>
    /// A conversation id that provably lands on <paramref name="shard"/> -
    /// probed through the real (per-process-randomized) hash, so the tests never
    /// depend on hash luck.
    /// </summary>
    private static string ConversationOnShard(int shard, int shardCount)
    {
        for (var i = 0; i < 10_000; i++)
        {
            var id = $"conv-{i}";
            if (ChannelTurnQueue.ShardFor(new TurnKey(Iss, Sub, Pid, id), shardCount) == shard)
            {
                return id;
            }
        }

        throw new InvalidOperationException($"No candidate conversation id hashed onto shard {shard}.");
    }

    private static TurnJob NewJob(string conversationId) => new()
    {
        Iss = Iss,
        Sub = Sub,
        Username = "tester",
        AllowedToolIds = new HashSet<string>(StringComparer.Ordinal),
        Pid = Pid,
        ConversationId = conversationId,
        UserMessageId = Guid.NewGuid().ToString("D"),
        AssistantMessageId = Guid.NewGuid().ToString("D"),
        AssistantSeq = 2,
        PlannedAt = DateTimeOffset.UtcNow,
        ModelId = "default",
        History = [],
    };

    /// <summary>
    /// A blocking <see cref="ITurnRunner"/>: records start/end per job, then
    /// parks on a per-job gate until the test releases it (or the stopping token
    /// cancels). <c>Gert.Testing</c>'s <c>FakeChatModel</c> is fixture-driven
    /// and cannot block - this fake exists precisely to hold a lane open.
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
