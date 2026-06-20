using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Tools;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Gert.Tools.Ports;
using NSubstitute;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for <see cref="WebFetchTool"/> - args parsing, the SSRF-block /
/// HTTP-failure outcomes surfacing as TOOL ERRORS (never a torn-down turn,
/// security F5), and the <c>max_chars</c> clip semantics. The port is the
/// boundary: <see cref="FakeWebFetcher"/> replays the shared fixtures (the
/// blocked metadata URL among them); a substitute covers the cap matrix.
/// </summary>
public sealed class WebFetchToolTests
{
    private static ToolInvocation Invoke(string argumentsJson) =>
        new() { Pid = "default", ArgumentsJson = argumentsJson };

    private static IWebFetcher FetcherReturning(string content)
    {
        var fetcher = Substitute.For<IWebFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WebFetchResult { Success = true, Content = content });
        return fetcher;
    }

    [Fact]
    public async Task Happy_path_returns_the_clipped_body_and_one_web_citation()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, new FakeWebFetcher());

        var result = await tool.ExecuteAsync(
            Invoke("{\"url\":\"https://example.test/sqlite-vec\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("sqlite-vec is a small, fast vector search extension");
        result.ResultJson.Should().Contain("\"truncated\":false");
        result.Stdout.Should().StartWith("fetched https://example.test/sqlite-vec");

        result.Citations.Should().ContainSingle();
        result.Citations[0].SourceType.Should().Be(CitationSourceType.Web);
        result.Citations[0].Locator.Should().Be("https://example.test/sqlite-vec");
    }

    [Fact]
    public async Task A_policy_blocked_url_is_a_tool_error_the_model_reads_not_a_fault()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, new FakeWebFetcher());

        var result = await tool.ExecuteAsync(
            Invoke("{\"url\":\"http://169.254.169.254/latest/meta-data/\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("URL blocked by fetch policy");
        result.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_http_failure_from_the_port_is_a_tool_error_too()
    {
        var fetcher = Substitute.For<IWebFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WebFetchResult { Success = false, Error = "fetch failed (404)" });
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, fetcher);

        var result = await tool.ExecuteAsync(Invoke("{\"url\":\"https://example.test/missing\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("fetch failed (404)");
    }

    [Fact]
    public async Task An_html_body_is_reduced_to_plain_text_before_the_clip()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, FetcherReturning(
            "<!doctype html><html><head><title>Docs</title><script>spy()</script></head>"
            + "<body><h1>Guide</h1><p>Useful body text.</p></body></html>"));

        var result = await tool.ExecuteAsync(Invoke("{\"url\":\"https://example.test/docs\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"extracted\":true");
        result.ResultJson.Should().Contain("Useful body text.");
        result.ResultJson.Should().NotContain("spy()");
        result.ResultJson.Should().NotContain("<h1>");
    }

    [Fact]
    public async Task A_non_html_body_passes_through_raw()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, FetcherReturning("{\"version\":\"1.2.3\"}"));

        var result = await tool.ExecuteAsync(Invoke("{\"url\":\"https://example.test/api\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"extracted\":false");
        result.ResultJson.Should().Contain("1.2.3");
    }

    [Fact]
    public async Task Max_chars_clips_the_content_and_flags_truncation()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, FetcherReturning(new string('x', 100)));

        var result = await tool.ExecuteAsync(
            Invoke("{\"url\":\"https://example.test/long\",\"max_chars\":10}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain($"\"content\":\"{new string('x', 10)}\"");
        result.ResultJson.Should().Contain("\"truncated\":true");
        result.ResultJson.Should().Contain("\"chars\":10");
        result.Stdout.Should().Contain("truncated");
    }

    [Fact]
    public async Task Default_cap_applies_when_max_chars_is_omitted()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, FetcherReturning(new string('y', WebFetchTool.DefaultMaxChars + 5)));

        var result = await tool.ExecuteAsync(Invoke("{\"url\":\"https://example.test/long\"}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain("\"truncated\":true");
        result.ResultJson.Should().Contain($"\"chars\":{WebFetchTool.DefaultMaxChars}");
    }

    [Fact]
    public async Task Max_chars_above_the_ceiling_is_clamped_not_errored()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, FetcherReturning(new string('z', WebFetchTool.MaxCharsCeiling + 5)));

        var result = await tool.ExecuteAsync(
            Invoke($"{{\"url\":\"https://example.test/long\",\"max_chars\":{int.MaxValue}}}"));

        result.Success.Should().BeTrue();
        result.ResultJson.Should().Contain($"\"chars\":{WebFetchTool.MaxCharsCeiling}");
        result.ResultJson.Should().Contain("\"truncated\":true");
    }

    [Fact]
    public async Task Missing_url_is_rejected()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, Substitute.For<IWebFetcher>());

        var result = await tool.ExecuteAsync(Invoke("{}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("url");
    }

    [Theory]
    [InlineData("\"ftp://example.test/file\"")]
    [InlineData("\"not a url\"")]
    [InlineData("\"/relative/path\"")]
    public async Task A_non_absolute_or_non_http_url_is_rejected_up_front(string urlJson)
    {
        var fetcher = Substitute.For<IWebFetcher>();
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, fetcher);

        var result = await tool.ExecuteAsync(Invoke($"{{\"url\":{urlJson}}}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("http");
        await fetcher.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_arguments_json_is_a_graceful_failure()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, Substitute.For<IWebFetcher>());

        var result = await tool.ExecuteAsync(Invoke("{not json"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid arguments");
    }

    [Fact]
    public async Task A_non_integer_max_chars_is_rejected()
    {
        var tool = new WebFetchTool(Gert.Testing.Proof.Validation, Substitute.For<IWebFetcher>());

        var result = await tool.ExecuteAsync(
            Invoke("{\"url\":\"https://example.test/\",\"max_chars\":\"lots\"}"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("max_chars");
    }
}
