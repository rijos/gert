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

        chunks.Should().HaveCount(4);
        chunks[0].TextDelta.Should().Be("Hel");
        chunks[1].TextDelta.Should().Be("lo");
        chunks[2].FinishReason.Should().Be("stop");
        chunks[2].TextDelta.Should().BeNull();
        // Usage arrives AFTER the finish chunk; the parser surfaces it as a
        // trailing usage chunk so the runner still observes the counts.
        chunks[3].FinishReason.Should().BeNull();
        chunks[3].TokenCount.Should().Be(7);
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
    public void Parse_UnterminatedToolCallArguments_DegradeToEmptyObject()
    {
        // Captured live from vLLM 0.22.1 + qwen3 tool parser with
        // chat_template_kwargs.enable_thinking=false and a no-argument call:
        // the arguments stream is a lone "{" that is never closed (the final
        // chunk carries finish_reason, an empty content delta, and no closing
        // fragment). Passing the fragment through poisons the turn twice —
        // the tool rejects it, and echoing it back inside the next round's
        // assistant tool_calls message makes the server 400 on its own output.
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1ee8120e235a4baeb37c043e","type":"function","index":0,"function":{"name":"get_datetime","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":""},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":296,"total_tokens":310,"completion_tokens":14}}""");

        var toolChunk = chunks.Single(c => c.ToolCall is not null);
        toolChunk.ToolCall!.Name.Should().Be("get_datetime");
        toolChunk.ToolCall.ArgumentsJson.Should().Be("{}",
            "unparseable argument fragments must degrade to an empty object, never flow downstream");

        chunks.Single(c => c.FinishReason is not null).FinishReason.Should().Be("tool_calls");
    }

    [Theory]
    [InlineData("{\"timezone\"")] // truncated mid-key
    [InlineData("\"not-an-object\"")] // valid JSON, wrong shape
    [InlineData("[1,2]")] // valid JSON, wrong shape
    [InlineData("not json at all")]
    public void Parse_MalformedToolCallArguments_DegradeToEmptyObject(string fragment)
    {
        // The fragment arrives as the wire-format arguments *string*.
        var encoded = System.Text.Json.JsonSerializer.Serialize(fragment);
        var line =
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_datetime","arguments":"""
            + encoded
            + """}}]},"finish_reason":null}]}""";

        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            line,
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        chunks.Single(c => c.ToolCall is not null).ToolCall!.ArgumentsJson.Should().Be("{}");
    }

    [Fact]
    public void Parse_WellFormedToolCallArguments_PassThroughVerbatim()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_datetime","arguments":"{\"timezone\": \"Europe/Amsterdam\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        chunks.Single(c => c.ToolCall is not null).ToolCall!.ArgumentsJson
            .Should().Be("""{"timezone": "Europe/Amsterdam"}""");
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

    [Fact]
    public void Parse_ReasoningContentDeltas_SurfaceAsReasoningChunks()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"reasoning_content":"hmm, "},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"reasoning_content":"let me think"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"Answer."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        chunks[0].ReasoningDelta.Should().Be("hmm, ");
        chunks[0].TextDelta.Should().BeNull();
        chunks[1].ReasoningDelta.Should().Be("let me think");
        chunks[2].TextDelta.Should().Be("Answer.");
        chunks[2].ReasoningDelta.Should().BeNull();
    }

    [Fact]
    public void Parse_ReasoningDeltas_Vllm022FieldName_SurfaceAsReasoningChunks()
    {
        // vLLM 0.22 renamed the output field `reasoning_content` → `reasoning`.
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"reasoning":"thinking…"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"Answer."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        chunks[0].ReasoningDelta.Should().Be("thinking…");
        chunks[1].TextDelta.Should().Be("Answer.");
    }

    [Fact]
    public void Parse_TrailingUsage_SurfacesPromptAndCompletionTokens()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":1234,"completion_tokens":56}}""");

        var usage = chunks.Last();
        usage.FinishReason.Should().BeNull();
        usage.PromptTokenCount.Should().Be(1234);
        usage.TokenCount.Should().Be(56);
    }

    [Fact]
    public void Parse_UsageBeforeFinish_PromptTokensRideTheFinishChunk()
    {
        var parser = new VllmStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":{"prompt_tokens":99,"completion_tokens":3}}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var finish = chunks.Single(c => c.FinishReason is not null);
        finish.PromptTokenCount.Should().Be(99);
        finish.TokenCount.Should().Be(3);
    }
}
