using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Unit tests for the pure update-&gt;<see cref="ChatResponseUpdate"/> mapper: content deltas,
/// incremental tool-call accumulation (name + fragmented arguments by index), the live name-first
/// intent, finish_reason, and usage extraction. Fixtures stay as
/// raw chunk JSON (the actual wire shapes captured from vLLM) and are lifted into the SDK's
/// <see cref="StreamingChatCompletionUpdate"/> via <see cref="ModelReaderWriter"/> - exactly what the
/// adapter hands the parser as the raw representation.
///
/// <para>
/// Arguments are carried as a parsed dictionary now (M.E.AI <see cref="FunctionCallContent"/>), so the
/// argument assertions are SEMANTIC (compact re-serialisation), not byte-verbatim - the wire echo is
/// the adapter's job and re-serialises anyway.
/// </para>
/// </summary>
public sealed class OpenAIStreamParserTests
{
    private static StreamingChatCompletionUpdate Update(string json) =>
        ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))!;

    private static List<ChatResponseUpdate> Run(OpenAIStreamParser parser, params string[] lines)
    {
        var updates = new List<ChatResponseUpdate>();
        foreach (var line in lines)
        {
            updates.AddRange(parser.Parse(Update(line)));
        }

        updates.AddRange(parser.Flush());
        return updates;
    }

    // ---- per-update content projections ----
    private static string? Text(ChatResponseUpdate u) => u.Contents.OfType<TextContent>().FirstOrDefault()?.Text;

    private static string? Reasoning(ChatResponseUpdate u) =>
        u.Contents.OfType<TextReasoningContent>().FirstOrDefault()?.Text;

    private static FunctionCallContent? Completed(ChatResponseUpdate u) =>
        u.Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Arguments is not null);

    private static FunctionCallContent? Intent(ChatResponseUpdate u) =>
        u.Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Arguments is null);

    private static string? Finish(ChatResponseUpdate u) => u.FinishReason?.ToString();

    private static long? OutTokens(ChatResponseUpdate u) =>
        u.Contents.OfType<UsageContent>().FirstOrDefault()?.Details.OutputTokenCount;

    private static long? InTokens(ChatResponseUpdate u) =>
        u.Contents.OfType<UsageContent>().FirstOrDefault()?.Details.InputTokenCount;

    private static string Args(FunctionCallContent call) => JsonSerializer.Serialize(call.Arguments);

    [Fact]
    public void Parse_ContentDeltas_ThenFinishWithUsage()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"content":"Hel"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"lo"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"completion_tokens":7}}""");

        updates.Should().HaveCount(4);
        Text(updates[0]).Should().Be("Hel");
        Text(updates[1]).Should().Be("lo");
        Finish(updates[2]).Should().Be("stop");
        Text(updates[2]).Should().BeNull();
        // Usage arrives AFTER the finish update; the parser surfaces it as a trailing usage update so
        // the consumer still observes the counts.
        Finish(updates[3]).Should().BeNull();
        OutTokens(updates[3]).Should().Be(7);
    }

    [Fact]
    public void Parse_UsageBeforeFinish_IsCarriedOntoFinishChunk()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":{"completion_tokens":3}}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var finish = updates.Single(u => Finish(u) is not null);
        Finish(finish).Should().Be("stop");
        OutTokens(finish).Should().Be(3);
    }

    [Fact]
    public void Parse_ToolCall_AccumulatesNameAndArguments()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"web_search","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":\"cat"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"s\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var call = updates.Select(Completed).Single(c => c is not null)!;
        call.CallId.Should().Be("call_1");
        call.Name.Should().Be("web_search");
        Args(call).Should().Be("""{"q":"cats"}""");

        Finish(updates.Single(u => Finish(u) is not null)).Should().Be("tool_calls");
    }

    [Fact]
    public void Parse_ToolCall_AnnouncesNameBeforeArgumentsFinish()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            // First fragment carries id + name (no/empty args yet).
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"make_artifact","arguments":""}}]},"finish_reason":null}]}""",
            // Argument fragments stream after - no further announcements.
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"name\":\"a"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":".md\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        // The intent is announced exactly once (null arguments), with the id the full call will use...
        var intent = updates.Select(Intent).Where(c => c is not null).Should().ContainSingle().Subject!;
        intent.CallId.Should().Be("call_1");
        intent.Name.Should().Be("make_artifact");

        // ...and it lands BEFORE the completed call, which carries the assembled args.
        var intentIdx = updates.FindIndex(u => Intent(u) is not null);
        var callIdx = updates.FindIndex(u => Completed(u) is not null);
        intentIdx.Should().BeLessThan(callIdx);
        Completed(updates[callIdx])!.CallId.Should().Be("call_1");
        Args(Completed(updates[callIdx])!).Should().Be("""{"name":"a.md"}""");
    }

    [Fact]
    public void Parse_WellFormedToolCallArguments_PassThrough()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_datetime","arguments":"{\"timezone\": \"Europe/Amsterdam\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        Args(updates.Select(Completed).Single(c => c is not null)!)
            .Should().Be("""{"timezone":"Europe/Amsterdam"}""");
    }

    [Fact]
    public void Parse_MultipleToolCalls_EmittedByIndexOrder()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"two","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"one","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var calls = updates.Select(Completed).Where(c => c is not null).Select(c => c!.Name).ToList();
        calls.Should().Equal("one", "two");
    }

    [Fact]
    public void Flush_WithoutInlineFinish_EmitsTerminalChunk()
    {
        var parser = new OpenAIStreamParser();
        parser.Parse(Update("""{"choices":[{"delta":{"content":"x"},"finish_reason":null}]}"""));
        var flushed = parser.Flush();
        flushed.Should().ContainSingle(u => Finish(u) == "stop");
    }

    [Fact]
    public void Parse_ReasoningContentDeltas_SurfaceAsReasoningChunks()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"reasoning_content":"hmm, "},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"reasoning_content":"let me think"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"Answer."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        Reasoning(updates[0]).Should().Be("hmm, ");
        Text(updates[0]).Should().BeNull();
        Reasoning(updates[1]).Should().Be("let me think");
        Text(updates[2]).Should().Be("Answer.");
        Reasoning(updates[2]).Should().BeNull();
    }

    [Fact]
    public void Parse_ReasoningDeltas_OpenAI022FieldName_SurfaceAsReasoningChunks()
    {
        // vLLM 0.22 renamed the output field `reasoning_content` -> `reasoning`.
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"reasoning":"thinking..."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"Answer."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        Reasoning(updates[0]).Should().Be("thinking...");
        Text(updates[1]).Should().Be("Answer.");
    }

    [Fact]
    public void Parse_TrailingUsage_SurfacesPromptAndCompletionTokens()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":1234,"completion_tokens":56}}""");

        var usage = updates[^1];
        Finish(usage).Should().BeNull();
        InTokens(usage).Should().Be(1234);
        OutTokens(usage).Should().Be(56);
    }

    [Fact]
    public void Parse_UsageBeforeFinish_PromptTokensRideTheFinishChunk()
    {
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}],"usage":{"prompt_tokens":99,"completion_tokens":3}}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        var finish = updates.Single(u => Finish(u) is not null);
        InTokens(finish).Should().Be(99);
        OutTokens(finish).Should().Be(3);
    }

    [Fact]
    public void Parse_AngleBracketContent_StreamsThroughUnmangled()
    {
        // The fixed qwen template no longer leaks <tool_call> markup into content, so there is no
        // hold-back: angle-bracket text streams straight through, split exactly as it arrived.
        var parser = new OpenAIStreamParser();
        var updates = Run(
            parser,
            """{"choices":[{"delta":{"content":"use <tool"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{"content":"box> for this"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        string.Concat(updates.Select(Text).Where(t => t is not null)).Should().Be("use <toolbox> for this");
    }
}
