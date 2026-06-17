using System.Text.Json;
using FluentAssertions;
using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Chat;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The DB read side: range paging (cursor/limit/HasMore), payload round-trip,
/// corrupt-row failure, and the orphan rule on thread reads.
/// </summary>
public sealed class ConversationReaderTests
{
    private const string Pid = "pid-1";
    private const string Conv = "conv-1";

    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IChatDatabaseProvider _provider = Substitute.For<IChatDatabaseProvider>();
    private readonly TestUserContext _user = new();
    private readonly TurnOptions _options = new();

    public ConversationReaderTests()
    {
        _provider
            .OpenAsync(_user.Iss, _user.Sub, Pid, Arg.Any<CancellationToken>())
            .Returns(_repo);
    }

    private ConversationReader NewReader() => new(_provider, _user, Options.Create(_options), TimeProvider.System);

    private static TurnEventRecord Row(long seq, ChatEvent evt) => new()
    {
        ConversationId = Conv,
        Seq = seq,
        Type = evt.Type.ToWireName(),
        PayloadJson = JsonSerializer.Serialize(evt, GertJsonOptions.Default),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Range_maps_rows_and_reports_cursor_and_has_more()
    {
        // The reader asks for limit+1 to learn HasMore; return exactly that many.
        _repo.ReadTurnEventsAsync(Conv, 10, 3, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Row(11, new DeltaEvent { Text = "a" }),
                Row(12, new DeltaEvent { Text = "b" }),
                Row(13, new DeltaEvent { Text = "c" }),
            });

        var range = await NewReader().ReadRangeAsync(Pid, Conv, afterSeq: 10, limit: 2);

        range.Events.Select(e => e.Seq).Should().Equal(11, 12);
        range.Events.Select(e => e.Event).Should().AllBeOfType<DeltaEvent>();
        ((DeltaEvent)range.Events[0].Event).Text.Should().Be("a");
        range.NextCursor.Should().Be(12);
        range.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Range_at_the_tail_has_no_more_and_null_cursor_when_empty()
    {
        _repo.ReadTurnEventsAsync(Conv, 13, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TurnEventRecord>());

        var range = await NewReader().ReadRangeAsync(Pid, Conv, afterSeq: 13, limit: 100);

        range.Events.Should().BeEmpty();
        range.NextCursor.Should().BeNull();
        range.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Polymorphic_payloads_round_trip_through_the_log()
    {
        _repo.ReadTurnEventsAsync(Conv, 0, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Row(1, new MessageStartEvent { MessageId = "m-1" }),
                Row(2, new ToolCallEvent { Id = "t-1", Kind = "rag", Status = ToolCallStatus.Running }),
                Row(3, new MessageEndEvent { TokenCount = 7 }),
            });

        var range = await NewReader().ReadRangeAsync(Pid, Conv, afterSeq: 0, limit: 10);

        range.Events[0].Event.Should().BeOfType<MessageStartEvent>()
            .Which.MessageId.Should().Be("m-1");
        range.Events[1].Event.Should().BeOfType<ToolCallEvent>()
            .Which.Kind.Should().Be("rag");
        range.Events[2].Event.Should().BeOfType<MessageEndEvent>()
            .Which.TokenCount.Should().Be(7);
    }

    [Fact]
    public async Task Corrupt_payload_row_throws_with_location()
    {
        _repo.ReadTurnEventsAsync(Conv, 0, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TurnEventRecord
                {
                    ConversationId = Conv,
                    Seq = 5,
                    Type = "delta",
                    PayloadJson = "{not json",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            });

        var act = () => NewReader().ReadRangeAsync(Pid, Conv, afterSeq: 0, limit: 10);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("seq 5");
    }

    [Fact]
    public async Task Thread_read_applies_the_orphan_rule()
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "m",
            CreatedAt = now,
            UpdatedAt = now,
        };

        Message Msg(string id, MessageStatus status, DateTimeOffset createdAt) => new()
        {
            Id = id,
            ConversationId = Conv,
            Role = MessageRole.Assistant,
            Content = "...",
            Status = status,
            CreatedAt = createdAt,
        };

        _repo.GetThreadAsync(Conv, Arg.Any<CancellationToken>())
            .Returns(new ConversationThread
            {
                Conversation = conversation,
                Messages = new[]
                {
                    Msg("fresh", MessageStatus.Streaming, now - TimeSpan.FromSeconds(30)),
                    Msg("orphaned", MessageStatus.Streaming, now - _options.MaxTurnDuration - TimeSpan.FromMinutes(1)),
                    Msg("done", MessageStatus.Complete, now - TimeSpan.FromHours(2)),
                },
            });

        var thread = await NewReader().GetThreadAsync(Pid, Conv);

        thread!.Messages.Single(m => m.Id == "fresh").Status.Should().Be(MessageStatus.Streaming);
        thread.Messages.Single(m => m.Id == "orphaned").Status.Should().Be(
            MessageStatus.Error,
            "a streaming row older than MaxTurnDuration is an abandoned turn (non-durable queue)");
        thread.Messages.Single(m => m.Id == "done").Status.Should().Be(MessageStatus.Complete);
    }
}
