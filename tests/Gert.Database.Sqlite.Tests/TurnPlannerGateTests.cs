using FluentAssertions;
using Gert.Agent;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Gert.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Service + adapter composition over a real on-disk <c>chat.db</c> (the
/// <see cref="IngestionPipelineTests"/> pattern): the planner's atomic 409 gate
/// (<c>ux_messages_streaming</c>, decisions section 11) under genuine cross-connection
/// concurrency - each <c>PlanAsync</c> opens its own connection - and the
/// expired-placeholder write-back made durable in the database.
/// </summary>
public class TurnPlannerGateTests
{
    private const string Pid = "default";

    private readonly FixedUserContext _user = new();

    private TurnPlanner PlannerFor(TempDataRoot root, TurnOptions options) => new(
        ProviderFixture.ChatProviderFor(root),
        _user,
        tools: [],
        Options.Create(options),
        Options.Create(new PromptOptions()),
        TimeProvider.System,
        instructions: null);

    [Fact]
    public async Task Concurrent_plans_for_one_conversation_yield_exactly_one_streaming_placeholder()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(_user.Iss, _user.Sub);

        var options = new TurnOptions();
        var conversationId = Guid.NewGuid().ToString("D");

        // Seed the conversation up front so the race is on the gate, not on
        // conversation materialisation.
        await using (var seed = await provider.OpenChatAsync(_user.Iss, _user.Sub, Pid))
        {
            await seed.InsertConversationAsync(new Conversation
            {
                Id = conversationId,
                Title = "race",
                ModelId = "default",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        async Task<TurnJob?> PlanOnce()
        {
            try
            {
                return await PlannerFor(root, options)
                    .PlanAsync(Pid, conversationId, Proof.Of(new SendMessageRequest { Content = "go" }));
            }
            catch (TurnInProgressException)
            {
                return null;
            }
        }

        // PlanAsync is fast, so one round rarely interleaves - loop. EITHER
        // interleaving (gate loss or fast-path 409) satisfies the assertion;
        // what can never happen is both succeeding (the pre-gate TOCTOU).
        for (var round = 0; round < 20; round++)
        {
            using var start = new SemaphoreSlim(0, 2);
            var t1 = Task.Run(async () =>
            {
                await start.WaitAsync();
                return await PlanOnce();
            });
            var t2 = Task.Run(async () =>
            {
                await start.WaitAsync();
                return await PlanOnce();
            });
            start.Release(2);
            var jobs = await Task.WhenAll(t1, t2);

            var winners = jobs.Where(j => j is not null).ToList();
            winners.Should().ContainSingle(
                $"round {round}: exactly one plan may hold the gate (the other gets the 409 rule)");

            await using var repo = await provider.OpenChatAsync(_user.Iss, _user.Sub, Pid);
            var messages = await repo.ListMessagesAsync(conversationId);
            messages.Count(m => m.Status == MessageStatus.Streaming).Should().Be(
                1,
                $"round {round}: the index admits exactly one streaming row");

            // Free the gate for the next round (the runner's finalize stand-in).
            await repo.FinalizeMessageAsync(
                winners[0]!.AssistantMessageId, "done", MessageStatus.Complete, null, null, null, null);
        }
    }

    [Fact]
    public async Task An_expired_placeholder_is_written_back_to_error_and_a_new_turn_proceeds()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(_user.Iss, _user.Sub);

        var options = new TurnOptions();
        var conversationId = Guid.NewGuid().ToString("D");
        var orphanId = Guid.NewGuid().ToString("D");

        await using (var seed = await provider.OpenChatAsync(_user.Iss, _user.Sub, Pid))
        {
            var now = DateTimeOffset.UtcNow;
            await seed.InsertConversationAsync(new Conversation
            {
                Id = conversationId,
                Title = "orphaned",
                ModelId = "default",
                CreatedAt = now,
                UpdatedAt = now,
            });
            // A dead turn's placeholder: streaming, but past the orphan horizon.
            await seed.InsertMessageAsync(new Message
            {
                Id = orphanId,
                ConversationId = conversationId,
                Role = MessageRole.Assistant,
                Content = "partial before the crash",
                Seq = 1,
                Status = MessageStatus.Streaming,
                CreatedAt = now - options.MaxTurnDuration - TimeSpan.FromMinutes(1),
            });
        }

        var job = await PlannerFor(root, options)
            .PlanAsync(Pid, conversationId, Proof.Of(new SendMessageRequest { Content = "retry" }));

        job.AssistantMessageId.Should().NotBeNullOrEmpty();

        await using var repo = await provider.OpenChatAsync(_user.Iss, _user.Sub, Pid);
        var messages = await repo.ListMessagesAsync(conversationId);

        // Durable, not just effective: the orphan row reads error IN THE DATABASE.
        var orphan = messages.Single(m => m.Id == orphanId);
        orphan.Status.Should().Be(MessageStatus.Error);
        orphan.Content.Should().Be("partial before the crash");

        // The new placeholder holds the gate alone.
        messages.Single(m => m.Status == MessageStatus.Streaming).Id.Should().Be(job.AssistantMessageId);
    }
}
