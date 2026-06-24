using FluentAssertions;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for <see cref="ReadDocumentTool"/> (read_document): full-text read of a project
/// document through the host's scripted <see cref="FakeToolHost.ScriptedDocumentResource"/> -
/// paging a large file, listing on an empty/unknown reference, and the binary-document note.
/// </summary>
public sealed class ReadDocumentToolTests
{
    private static ReadDocumentTool Tool() => new(Gert.Testing.Proof.Validation);

    private static FakeToolHost HostWith(params (string Name, string Text)[] docs)
    {
        var host = new FakeToolHost();
        foreach (var (name, text) in docs)
        {
            host.Documents.Texts[name] = text;
        }

        return host;
    }

    [Fact]
    public async Task Returns_the_full_text_of_a_named_document()
    {
        var host = HostWith(("notes.txt", "the whole file content"));

        var result = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"notes.txt\"}" }, host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("the whole file content");
        result.ResultJson.Should().Contain("\"total_chars\":22");
        result.ResultJson.Should().Contain("\"has_more\":false");
    }

    [Fact]
    public async Task Pages_a_large_document_with_offset_and_max_chars()
    {
        var host = HostWith(("big.txt", "ABCDEFGHIJ"));

        var first = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"big.txt\",\"max_chars\":4}" }, host);
        first.Success.Should().BeTrue();
        first.ResultJson.Should().Contain("ABCD");
        first.ResultJson.Should().Contain("\"has_more\":true");
        first.ResultJson.Should().Contain("\"next_offset\":4");

        var next = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"big.txt\",\"offset\":4}" }, host);
        next.ResultJson.Should().Contain("EFGHIJ");
        next.ResultJson.Should().Contain("\"has_more\":false");
    }

    [Fact]
    public async Task Empty_doc_lists_available_documents()
    {
        var host = HostWith(("a.json", "1"), ("b.csv", "2"));

        var result = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"\"}" }, host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("a.json");
        result.ResultJson.Should().Contain("b.csv");
    }

    [Fact]
    public async Task Unknown_doc_returns_the_available_list()
    {
        var host = HostWith(("a.json", "1"));

        var result = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"missing.json\"}" }, host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("a.json");
        result.ResultJson.Should().Contain("No document matched");
    }

    [Fact]
    public async Task Binary_document_returns_a_pointer_to_search_documents()
    {
        var host = new FakeToolHost();
        host.Documents.Binary.Add("scan.pdf");

        var result = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"scan.pdf\"}" }, host);

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("search_documents");
    }

    [Fact]
    public async Task Negative_offset_fails_arg_validation()
    {
        var host = HostWith(("a.json", "1"));

        var result = await Tool().RunAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = "{\"doc\":\"a.json\",\"offset\":-1}" }, host);

        result.Success.Should().BeFalse();
    }
}
