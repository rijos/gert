using System.ClientModel.Primitives;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Tools;
using OpenAI.Chat;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Unit tests for the pure update->chunk mapper: content deltas, incremental tool-call
/// accumulation (name + fragmented arguments by index), finish_reason, and usage
/// extraction. Fixtures stay as raw chunk JSON (the actual wire shapes captured from
/// vLLM) and are lifted into the SDK's <see cref="StreamingChatCompletionUpdate"/> via
/// <see cref="ModelReaderWriter"/> - exactly what the client feeds the parser.
/// </summary>
public sealed class OpenAIStreamParserTests
{
    private static StreamingChatCompletionUpdate Update(string json) =>
        ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))!;

    private static List<ChatModelChunk> Run(OpenAIStreamParser parser, params string[] lines)
    {
        var chunks = new List<ChatModelChunk>();
        foreach (var line in lines)
        {
            chunks.AddRange(parser.Parse(Update(line)));
        }

        chunks.AddRange(parser.Flush());
        return chunks;
    }

    [Fact]
    public void Parse_ContentDeltas_ThenFinishWithUsage()
    {
        var parser = new OpenAIStreamParser();
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
        var parser = new OpenAIStreamParser();
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
        var parser = new OpenAIStreamParser();
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
    public void Parse_ToolCall_AnnouncesNameBeforeArgumentsFinish()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            // First fragment carries id + name (no/empty args yet).
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"make_artifact","arguments":""}}]},"finish_reason":null}]}""",
            // Argument fragments stream after - no further announcements.
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"name\":\"a"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":".md\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        // The intent is announced exactly once, with the id the full call will use...
        var start = chunks.Where(c => c.ToolCallStart is not null).Should().ContainSingle().Subject;
        start.ToolCallStart!.Id.Should().Be("call_1");
        start.ToolCallStart.Name.Should().Be("make_artifact");

        // ...and it lands BEFORE the completed call, which carries the assembled args.
        var startIdx = chunks.FindIndex(c => c.ToolCallStart is not null);
        var callIdx = chunks.FindIndex(c => c.ToolCall is not null);
        startIdx.Should().BeLessThan(callIdx);
        chunks[callIdx].ToolCall!.Id.Should().Be("call_1");
        chunks[callIdx].ToolCall!.ArgumentsJson.Should().Be("""{"name":"a.md"}""");
    }

    [Fact]
    public void Parse_UnterminatedToolCallArguments_DegradeToEmptyObject()
    {
        // Captured live from vLLM 0.22.1 + qwen3 tool parser with
        // chat_template_kwargs.enable_thinking=false and a no-argument call:
        // the arguments stream is a lone "{" that is never closed (the final
        // chunk carries finish_reason, an empty content delta, and no closing
        // fragment). Passing the fragment through poisons the turn twice -
        // the tool rejects it, and echoing it back inside the next round's
        // assistant tool_calls message makes the server 400 on its own output.
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1ee8120e235a4baeb37c043e","type":"function","index":0,"function":{"name":"get_datetime","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":""},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":296,"total_tokens":310,"completion_tokens":14}}""");

        var toolChunk = chunks.Single(c => c.ToolCall is not null);
        toolChunk.ToolCall!.Name.Should().Be("get_datetime");
        toolChunk.ToolCall.ArgumentsJson.Should().Be(
            "{}",
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

        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            line,
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        chunks.Single(c => c.ToolCall is not null).ToolCall!.ArgumentsJson.Should().Be("{}");
    }

    [Fact]
    public void Parse_WellFormedToolCallArguments_PassThroughVerbatim()
    {
        var parser = new OpenAIStreamParser();
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
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"two","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"one","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var calls = chunks.Where(c => c.ToolCall is not null).Select(c => c.ToolCall!.Name).ToList();
        calls.Should().Equal("one", "two");
    }

    [Fact]
    public void Flush_WithoutInlineFinish_EmitsTerminalChunk()
    {
        var parser = new OpenAIStreamParser();
        parser.Parse(Update("""{"choices":[{"delta":{"content":"x"},"finish_reason":null}]}"""));
        var flushed = parser.Flush();
        flushed.Should().ContainSingle(c => c.FinishReason == "stop");
    }

    [Fact]
    public void Parse_ReasoningContentDeltas_SurfaceAsReasoningChunks()
    {
        var parser = new OpenAIStreamParser();
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
    public void Parse_ReasoningDeltas_OpenAI022FieldName_SurfaceAsReasoningChunks()
    {
        // vLLM 0.22 renamed the output field `reasoning_content` -> `reasoning`.
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"reasoning":"thinking..."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"Answer."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        chunks[0].ReasoningDelta.Should().Be("thinking...");
        chunks[1].TextDelta.Should().Be("Answer.");
    }

    [Fact]
    public void Parse_TrailingUsage_SurfacesPromptAndCompletionTokens()
    {
        var parser = new OpenAIStreamParser();
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
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":{"prompt_tokens":99,"completion_tokens":3}}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var finish = chunks.Single(c => c.FinishReason is not null);
        finish.PromptTokenCount.Should().Be(99);
        finish.TokenCount.Should().Be(3);
    }

    // -- Leaked tool-call markup (chat-and-tools.md section tool-call robustness) --
    // When the server-side tool parser misses a call, the raw <tool_call> markup
    // leaks into content deltas. The hold-back salvages well-formed leaks into
    // real tool calls and drops the mangled rest - neither reaches the text.

    [Fact]
    public void Parse_LeakedXmlToolCall_IsSalvagedNotSurfaced()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"Let me look.\n<tool_call>\n<function=web_search>\n<parameter=query>\ntop stories\n</parameter>\n</function>\n</tool_call>"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("Let me look.\n");

        var call = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        call.Name.Should().Be("web_search");
        call.ArgumentsJson.Should().Be("""{"query":"top stories"}""");
        parser.SalvagedToolCalls.Should().Be(1);
        parser.DroppedLeakChars.Should().Be(0);
    }

    [Fact]
    public void Parse_LeakedJsonToolCall_SplitAcrossDeltas_IsSalvaged()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"<tool"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"_call>{\"name\": \"get_datetime\", \"arguments\": {\"timezone\": \"Europe/Amsterdam\"}}</tool_"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"call>"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        chunks.Where(c => c.TextDelta is not null).Should().BeEmpty();
        var call = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        call.Name.Should().Be("get_datetime");
        call.ArgumentsJson.Should().Contain("Europe/Amsterdam");
    }

    [Fact]
    public void Parse_MangledLeakedMarkup_IsDroppedAndCounted()
    {
        // The drifted form a struggling model actually emitted: wrong tags that
        // neither the server parser nor the salvage can read - drop, never show.
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"\n\n<tool_call>\n<user_search>\n<parameter>query>\ntop stories\n</parameter>\n</function>"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("\n\n");
        chunks.Should().NotContain(c => c.ToolCall != null);
        parser.SalvagedToolCalls.Should().Be(0);
        parser.DroppedLeakChars.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_TextAfterLeakedCall_ResumesStreaming()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"before <tool_call><function=f1></function></tool_call> after"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("before  after");
        chunks.Single(c => c.ToolCall is not null).ToolCall!.Name.Should().Be("f1");
    }

    [Fact]
    public void Parse_AngleBracketText_ThatIsNotAnOpener_StreamsThrough()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"use <tool"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"box> for this"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("use <toolbox> for this");
    }

    [Fact]
    public void Parse_HeldOpenerPrefix_AtStreamEnd_EmitsAsText()
    {
        // A reply ending in a partial "<tool_call" prefix is plain text once the
        // stream finishes without completing the opener.
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"see <tool_ca"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("see <tool_ca");
    }

    [Fact]
    public void Parse_UnclosedLeakAtStreamEnd_IsStillSalvaged()
    {
        var parser = new OpenAIStreamParser();
        var chunks = Run(
            parser,
            """{"choices":[{"delta":{"content":"<tool_call>\n<function=read_artifact>\n<parameter=name>\nindex.html\n</parameter>\n<parameter=range>\n[1, -1]\n</parameter>\n</function>"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var call = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        call.Name.Should().Be("read_artifact");
        call.ArgumentsJson.Should().Be("""{"name":"index.html","range":[1,-1]}""");
    }
}
