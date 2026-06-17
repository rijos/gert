using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Service.Documents;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Gert.Tools.Builtin;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// Unit tests for <see cref="SaveMemoryTool"/> - args parsing, the pass-through
/// to <see cref="IMemoryService.UpsertAsync"/> (always unpinned: pinning stays a
/// human action in the knowledge panel), and the
/// <see cref="ValidationException"/> -> correctable-tool-error mapping. Any other
/// service exception propagates to the runner's generic per-call catch.
/// </summary>
public sealed class SaveMemoryToolTests
{
    private static ToolInvocation Invoke(string argumentsJson) =>
        new() { Pid = "default", ArgumentsJson = argumentsJson };

    private static MemoryEntry Entry(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Content = "User prefers tabs over spaces.",
        Pinned = false,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Happy_path_saves_one_unpinned_entry_and_returns_its_id()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.UpsertAsync("default", Arg.Any<CreateMemoryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Entry("mem-1", call.Arg<CreateMemoryRequest>().Title));
        var tool = new SaveMemoryTool(memory);

        var result = await tool.ExecuteAsync(
            Invoke("{\"title\":\"Editor preference\",\"content\":\"User prefers tabs over spaces.\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"id\":\"mem-1\"");
        result.ResultJson.Should().Contain("\"saved\":true");
        result.Stdout.Should().Be("saved memory: Editor preference");

        await memory.Received(1).UpsertAsync(
            "default",
            Arg.Is<CreateMemoryRequest>(r =>
                r.Title == "Editor preference"
                && r.Content == "User prefers tabs over spaces."
                && r.Pinned == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_validation_failure_is_a_correctable_tool_error()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.UpsertAsync(Arg.Any<string>(), Arg.Any<CreateMemoryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ValidationException(ValidationResult.Failure(
                [new ValidationError { Property = "Title", Message = "must be at most 200 characters" }])));
        var tool = new SaveMemoryTool(memory);

        var result = await tool.ExecuteAsync(
            Invoke("{\"title\":\"way too long\",\"content\":\"x\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Title").And.Contain("200");
    }

    [Fact]
    public async Task Any_other_service_exception_propagates_to_the_runner()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.UpsertAsync(Arg.Any<string>(), Arg.Any<CreateMemoryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("embeddings upstream down"));
        var tool = new SaveMemoryTool(memory);

        var act = () => tool.ExecuteAsync(Invoke("{\"title\":\"t\",\"content\":\"c\"}"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("{}", "title")]
    [InlineData("{\"title\":\"t\"}", "content")]
    [InlineData("{\"title\":\"  \",\"content\":\"c\"}", "title")]
    [InlineData("{\"title\":\"t\",\"content\":\"\"}", "content")]
    public async Task Missing_or_blank_arguments_are_rejected_before_the_service(
        string argumentsJson,
        string expectedMention)
    {
        var memory = Substitute.For<IMemoryService>();
        var tool = new SaveMemoryTool(memory);

        var result = await tool.ExecuteAsync(Invoke(argumentsJson));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(expectedMention);
        await memory.DidNotReceive().UpsertAsync(
            Arg.Any<string>(), Arg.Any<CreateMemoryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_arguments_json_is_a_graceful_failure()
    {
        var tool = new SaveMemoryTool(Substitute.For<IMemoryService>());

        var result = await tool.ExecuteAsync(Invoke("{not json"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid arguments");
    }
}
