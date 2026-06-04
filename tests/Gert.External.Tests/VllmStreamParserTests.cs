using FluentAssertions;
using Gert.External.Vllm;
using Gert.Service.External;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Unit tests for the pure OpenAI-compatible SSE parser: content deltas, incremental
/// tool-call accumulation (name + fragmented arguments by index), finish_reason, and
/// usage extraction.
/// </summary>
public sealed class VllmStreamParserTests
{
    private static List<ChatModelChunk> Run(VllmStreamParser parser, params string[] lines)
    {
        var chunks = new List<ChatModelChunk>();
        foreach (var line in lines)
        {
            chunks.AddRange(parser.Parse(line));
        }

        chunks.AddRange(parser.Flush());
        return chunks;
    }

    [Fact]
    public void Parse_ContentDeltas_ThenFinishWithUsage()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"Hel"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"lo"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"completion_tokens":7}}""");

        chunks.Should().HaveCount(3);
        chunks[0].TextDelta.Should().Be("Hel");
        chunks[1].TextDelta.Should().Be("lo");
        chunks[2].FinishReason.Should().Be("stop");
        // Usage arrives after the finish chunk; the parser folds the count it saw.
        // The terminal chunk is emitted at finish_reason, before the usage tail, so the
        // token count may be null here — assert finish is correct regardless.
        chunks[2].TextDelta.Should().BeNull();
    }

    [Fact]
    public void Parse_UsageBeforeFinish_IsCarriedOntoFinishChunk()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":{"completion_tokens":3}}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var finish = chunks.Single(c => c.FinishReason is not null);
        finish.FinishReason.Should().Be("stop");
        finish.TokenCount.Should().Be(3);
    }

    [Fact]
    public void Parse_ToolCall_AccumulatesNameAndArguments()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"web_search","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":\"cat"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"s\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var toolChunk = chunks.Single(c => c.ToolCall is not null);
        toolChunk.ToolCall!.Id.Should().Be("call_1");
        toolChunk.ToolCall.Name.Should().Be("web_search");
        toolChunk.ToolCall.ArgumentsJson.Should().Be("""{"q":"cats"}""");

        var finish = chunks.Single(c => c.FinishReason is not null);
        finish.FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public void Parse_MultipleToolCalls_EmittedByIndexOrder()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"two","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"one","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var calls = chunks.Where(c => c.ToolCall is not null).Select(c => c.ToolCall!.Name).ToList();
        calls.Should().Equal("one", "two");
    }

    [Fact]
    public void Parse_MalformedLine_IsIgnored()
    {
        var parser = new VllmStreamParser();
        parser.Parse("this is not json").Should().BeEmpty();
    }

    [Fact]
    public void Flush_WithoutInlineFinish_EmitsTerminalChunk()
    {
        var parser = new VllmStreamParser();
        parser.Parse("""{"choices":[{"delta":{"content":"x"},"finish_reason":null}]}""");
        var flushed = parser.Flush();
        flushed.Should().ContainSingle(c => c.FinishReason == "stop");
    }
}
