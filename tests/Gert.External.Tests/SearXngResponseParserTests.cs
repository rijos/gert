using FluentAssertions;
using Gert.External.Search;
using Xunit;

namespace Gert.External.Tests;

/// <summary>Unit tests for the SearXNG JSON response parser.</summary>
public sealed class SearXngResponseParserTests
{
    private const string Sample = """
    {
      "results": [
        { "title": "First", "url": "https://a.example/1", "content": "snippet 1" },
        { "title": "Second", "url": "https://b.example/2", "content": "snippet 2" },
        { "title": "NoUrl" }
      ]
    }
    """;

    [Fact]
    public void Parse_MapsTitleUrlSnippet()
    {
        var results = SearXngResponseParser.Parse(Sample, maxResults: 10);

        results.Should().HaveCount(2); // the url-less row is dropped
        results[0].Title.Should().Be("First");
        results[0].Url.Should().Be("https://a.example/1");
        results[0].Snippet.Should().Be("snippet 1");
    }

    [Fact]
    public void Parse_CapsToMaxResults()
    {
        var results = SearXngResponseParser.Parse(Sample, maxResults: 1);
        results.Should().ContainSingle();
    }

    [Fact]
    public void Parse_ZeroMax_ReturnsEmpty()
    {
        SearXngResponseParser.Parse(Sample, 0).Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoResultsArray_ReturnsEmpty()
    {
        SearXngResponseParser.Parse("""{"query":"x"}""", 5).Should().BeEmpty();
    }
}
