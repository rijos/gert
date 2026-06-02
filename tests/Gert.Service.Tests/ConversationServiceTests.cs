using FluentAssertions;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Conversations;
using Gert.Service.Database;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// CRUD + scoping tests for <see cref="ConversationService"/>. The repo and
/// provider are NSubstitute fakes; the user is a fixed <see cref="TestUserContext"/>.
/// </summary>
public sealed class ConversationServiceTests
{
    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IDatabaseProvider _provider = Substitute.For<IDatabaseProvider>();
    private readonly TestUserContext _user = new();

    public ConversationServiceTests()
    {
        _provider
            .OpenChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);
    }

    private ConversationService NewService() => new(_provider, _user);

    [Fact]
    public async Task Create_sets_id_timestamps_and_inserts()
    {
        var sut = NewService();
        var before = DateTimeOffset.UtcNow;

        var result = await sut.CreateAsync(
            "default",
            new CreateConversationRequest { Title = "Planning", ModelId = "qwen" });

        result.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(result.Id, out _).Should().BeTrue("ids are UUIDs");
        result.Title.Should().Be("Planning");
        result.ModelId.Should().Be("qwen");
        result.CreatedAt.Should().BeOnOrAfter(before);
        result.UpdatedAt.Should().Be(result.CreatedAt);
        result.Archived.Should().BeFalse();

        await _repo.Received(1).InsertConversationAsync(
            Arg.Is<Conversation>(c => c.Id == result.Id && c.Title == "Planning"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_with_no_title_uses_a_default_title()
    {
        var sut = NewService();

        var result = await sut.CreateAsync("default", new CreateConversationRequest());

        result.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task List_returns_repo_rows()
    {
        var rows = new[] { Conv("a"), Conv("b") };
        _repo.ListConversationsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Conversation>)rows);

        var result = await NewService().ListAsync("default");

        result.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task Get_returns_the_thread()
    {
        var thread = new ConversationThread { Conversation = Conv("a") };
        _repo.GetThreadAsync("a", Arg.Any<CancellationToken>()).Returns(thread);

        var result = await NewService().GetAsync("default", "a");

        result.Should().BeSameAs(thread);
    }

    [Fact]
    public async Task Update_applies_partial_change_and_bumps_updated_at()
    {
        var existing = Conv("a") with { Title = "Old", UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        _repo.GetConversationAsync("a", Arg.Any<CancellationToken>()).Returns(existing);

        var result = await NewService().UpdateAsync(
            "default", "a", new UpdateConversationRequest { Title = "New" });

        result.Should().NotBeNull();
        result!.Title.Should().Be("New");
        result.ModelId.Should().Be(existing.ModelId, "unsupplied fields are preserved");
        result.UpdatedAt.Should().BeAfter(existing.UpdatedAt);

        await _repo.Received(1).UpdateConversationAsync(
            Arg.Is<Conversation>(c => c.Title == "New"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_missing_conversation_returns_null_and_does_not_write()
    {
        _repo.GetConversationAsync("missing", Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);

        var result = await NewService().UpdateAsync(
            "default", "missing", new UpdateConversationRequest { Title = "x" });

        result.Should().BeNull();
        await _repo.DidNotReceive().UpdateConversationAsync(
            Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_delegates_to_repo()
    {
        _repo.DeleteConversationAsync("a", Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewService().DeleteAsync("default", "a");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Every_operation_opens_the_repo_for_the_user_context_identity_only()
    {
        _repo.ListConversationsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Conversation>)Array.Empty<Conversation>());

        await NewService().ListAsync("proj-1");
        await NewService().CreateAsync("proj-1", new CreateConversationRequest());

        // Identity always comes from IUserContext — never widened by a parameter.
        await _provider.Received().OpenChatAsync(
            _user.Iss, _user.Sub, "proj-1", Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().OpenChatAsync(
            Arg.Is<string>(s => s != _user.Iss), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().OpenChatAsync(
            Arg.Any<string>(), Arg.Is<string>(s => s != _user.Sub), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repo_is_disposed_after_use()
    {
        _repo.ListConversationsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Conversation>)Array.Empty<Conversation>());

        await NewService().ListAsync("default");

        await _repo.Received(1).DisposeAsync();
    }

    private static Conversation Conv(string id) => new()
    {
        Id = id,
        Title = "t",
        ModelId = "m",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
