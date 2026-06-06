using FluentAssertions;
using Gert.External.Vllm;
using Gert.Service.Chat;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// LIVE integration against a real vLLM server — skipped unless
/// <c>GERT_VLLM_URL</c> is set (CI has no GPU box). Drives the REAL wire path:
/// <see cref="VllmChatRequestBuilder"/> → HTTP SSE → <see cref="VllmStreamParser"/>,
/// then <see cref="ArtifactExtractor"/> over the assembled completion — proving
/// the model can be steered into named html/py/md fences and that our
/// request-shape (chat_template_kwargs, usage) holds against the deployed build.
///
/// Run:  GERT_VLLM_URL=http://192.168.10.99:8000 [GERT_VLLM_MODEL=qwen36] dotnet test \
///       --filter FullyQualifiedName~VllmLiveIntegration
/// </summary>
public sealed class VllmLiveIntegrationTests
{
    private static VllmChatModelClient? CreateClient(out string baseUrl)
    {
        baseUrl = Environment.GetEnvironmentVariable("GERT_VLLM_URL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // The client appends /v1/...; accept a configured url with or without it.
        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal))
        {
            baseUrl = baseUrl[..^3];
        }

        var options = new VllmOptions
        {
            BaseUrl = baseUrl,
            ChatModelId = Environment.GetEnvironmentVariable("GERT_VLLM_MODEL") ?? "qwen36",
            RequestTimeoutSeconds = 180,
        };

        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };

        return new VllmChatModelClient(
            http, Options.Create(options), NullLogger<VllmChatModelClient>.Instance);
    }

    [Fact]
    public async Task Live_model_produces_named_html_py_and_md_artifacts()
    {
        var client = CreateClient(out var baseUrl);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        // The screenshot regression: a NATURAL request, with the name= fence
        // convention taught ONLY by the built-in system prompt (exactly what
        // TurnPlanner sends) — the model must opt its files into the canvas
        // without the user ever mentioning the syntax.
        var request = new ChatCompletionRequest
        {
            ModelId = "default", // → VllmOptions.ChatModelId
            Messages =
            [
                new ChatModelMessage { Role = "system", Content = SystemPrompts.Canvas },
                new ChatModelMessage
                {
                    Role = "user",
                    Content =
                        "Make me three small standalone files: a minimal HTML5 starter page " +
                        "with an <h1>, a Python script with a fibonacci function, and a short " +
                        "Markdown note describing both. Keep each under 15 lines.",
                },
            ],
            // Deterministic-ish + fast: no sampling spread, no thinking detour.
            Temperature = 0,
            MaxTokens = 1600,
            EnableThinking = false,
        };

        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        int? completionTokens = null;
        int? promptTokens = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await foreach (var chunk in client!.StreamAsync(request, cts.Token))
        {
            content.Append(chunk.TextDelta);
            reasoning.Append(chunk.ReasoningDelta);
            completionTokens ??= chunk.TokenCount;
            completionTokens = chunk.TokenCount ?? completionTokens;
            promptTokens = chunk.PromptTokenCount ?? promptTokens;
        }

        // The real wire path delivered text and the trailing usage chunk
        // (prompt tokens are what the composer's context ring runs on).
        content.Length.Should().BeGreaterThan(0, $"the model at {baseUrl} should reply");
        completionTokens.Should().NotBeNull("stream_options.include_usage is always requested");
        promptTokens.Should().BeGreaterThan(0, "vLLM reports prompt_tokens on the usage tail");

        // enable_thinking=false reached the template: no reasoning streamed.
        reasoning.Length.Should().Be(0, "chat_template_kwargs.enable_thinking=false suppresses thinking");

        // The extractor lifts all three kinds out of the real completion — the
        // model picked its own filenames; the system prompt supplied the syntax.
        var artifacts = ArtifactExtractor.Extract(content.ToString());
        artifacts.Select(a => a.Kind).Should().Contain(
            [ArtifactKind.Html, ArtifactKind.Py, ArtifactKind.Md],
            $"the canvas convention must steer real fences (got: {content})");
        artifacts.Single(a => a.Kind == ArtifactKind.Html).Content.Should().Contain("<h1");
        artifacts.Single(a => a.Kind == ArtifactKind.Py).Content.Should().Contain("def ");
        artifacts.Should().OnlyContain(a => a.Name.Length > 0);
    }

    [Fact]
    public async Task Live_tool_round_trip_with_thinking_disabled_completes()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        // The clock-tool crash (2026-06-06): with enable_thinking=false the
        // qwen3 tool parser on vLLM 0.22 streams a lone, unterminated "{" for a
        // no-argument get_datetime call. Unnormalized, that fragment fails the
        // tool AND 400s the second round when echoed back inside the assistant
        // tool_calls message. This drives the REAL wire path through both
        // rounds: the parsed arguments must be valid JSON, and the follow-up
        // request carrying them must complete.
        var clock = new ClockTool(TimeProvider.System);
        var tools = new List<ChatToolSpec>
        {
            new()
            {
                Name = clock.Name,
                Description = clock.Description,
                ParametersSchema = clock.ParametersSchema,
            },
        };

        var messages = new List<ChatModelMessage>
        {
            // The exact user phrasing that reproduced the unterminated fragment.
            new() { Role = "user", Content = "what is the local time?" },
        };

        // Round 1: the model must call the tool, and the accumulated arguments
        // must come out of the parser as a well-formed JSON object.
        var round1 = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages = messages,
            Tools = tools,
            Temperature = 0,
            MaxTokens = 400,
            EnableThinking = false,
        };

        var toolCalls = new List<ChatModelToolCall>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await foreach (var chunk in client!.StreamAsync(round1, cts.Token))
        {
            if (chunk.ToolCall is not null)
            {
                toolCalls.Add(chunk.ToolCall);
            }
        }

        var call = toolCalls.Should().ContainSingle("the model should call get_datetime").Subject;
        call.Name.Should().Be("get_datetime");
        var parsed = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        parsed.RootElement.ValueKind.Should().Be(
            System.Text.Json.JsonValueKind.Object,
            "the parser must never surface an argument fragment");

        // The real tool executes the normalized arguments without erroring.
        var outcome = await clock.ExecuteAsync(
            new ToolInvocation { Pid = "default", ArgumentsJson = call.ArgumentsJson }, cts.Token);
        outcome.Success.Should().BeTrue("'{}' means UTC-by-default, not invalid arguments");

        // Round 2: echo the assistant tool_calls + tool result back, exactly as
        // the tool loop does — the request must not 400.
        messages.Add(new ChatModelMessage { Role = "assistant", Content = null, ToolCalls = toolCalls });
        messages.Add(new ChatModelMessage
        {
            Role = "tool",
            Content = outcome.ResultJson ?? string.Empty,
            ToolCallId = call.Id,
        });

        var round2 = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages = messages,
            Tools = tools,
            Temperature = 0,
            MaxTokens = 400,
            EnableThinking = false,
        };

        var answer = new System.Text.StringBuilder();
        await foreach (var chunk in client.StreamAsync(round2, cts.Token))
        {
            answer.Append(chunk.TextDelta);
        }

        answer.Length.Should().BeGreaterThan(0, "the second round must complete with an answer");
    }

    [Fact]
    public async Task Live_model_plans_multi_step_work_with_set_todos()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        // The planning contract: a natural multi-step request plus the bare
        // tool description must steer the model into set_todos, and the REAL
        // TodoTool must accept whatever arguments the parser assembled. This
        // is the within-turn half of the todo story (the cross-turn half is
        // the reminder test below).
        var todo = new TodoTool();
        var tools = ToolSpecs(todo);

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = SystemPrompts.Canvas },
            new()
            {
                Role = "user",
                Content =
                    "Write three very short haiku: one about spring, one about summer, one " +
                    "about winter. Plan the steps with your todo list before you start.",
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var (answer, todoCalls, log) = await RunTodoLoopAsync(client!, messages, tools, todo, cts.Token);

        // The model planned with the tool and still produced the actual work.
        todoCalls.Should().NotBeEmpty("a multi-step request with the todo tool offered should be planned");
        answer.Should().NotBeEmpty("the turn must end in a final answer, not a tool call");

        // Every accepted call round-tripped the real tool; the last list should
        // carry the three planned steps (the cap test: short, ordered, valid).
        var lastArgs = System.Text.Json.JsonDocument.Parse(todoCalls[^1].ArgumentsJson);
        lastArgs.RootElement.GetProperty("todos").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Live_todo_reminder_revives_unfinished_list_across_turns()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        // The cross-turn gap: TurnPlanner rebuilds history as role+content only,
        // so the todo list set via tool calls in turn 1 is INVISIBLE in turn 2.
        // This renders turn 2 exactly as the planner would — assistant content
        // without its tool_calls — and appends SystemPrompts.TodoReminder to the
        // new user message. The assistant content is deliberately vague about
        // which steps are done: ONLY the reminder says winter is the one left,
        // so a winter haiku in the answer proves the snapshot was read.
        var todo = new TodoTool();
        var tools = ToolSpecs(todo);

        const string snapshot =
            """{"todos":[{"text":"write the spring haiku","status":"done"},{"text":"write the summer haiku","status":"done"},{"text":"write the winter haiku","status":"pending"}]}""";

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = SystemPrompts.Canvas },
            new()
            {
                Role = "user",
                Content =
                    "Write three haiku — spring, summer, winter — one at a time, and track " +
                    "the steps with your todo list.",
            },
            new()
            {
                Role = "assistant",
                Content = "I'm tracking the haiku in my todo list and have made progress. " +
                          "Say 'continue' for the next one.",
            },
            new()
            {
                Role = "user",
                Content = "continue\n\n" + SystemPrompts.TodoReminder(snapshot),
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var (answer, todoCalls, log) = await RunTodoLoopAsync(client!, messages, tools, todo, cts.Token);

        // The reminder is the only place winter is marked as the remaining step.
        answer.Should().NotBeEmpty("the turn must end in a final answer");
        answer.Should().ContainEquivalentOf("winter", "the pending item lives only in the reminder snapshot");

        // If the model also updated its list (the reminder asks it to), the new
        // list must be a continuation of the snapshot — not a fresh start.
        if (todoCalls.Count > 0)
        {
            todoCalls[^1].ArgumentsJson.Should().ContainEquivalentOf("winter",
                "an updated list continues the snapshot rather than replacing the plan");
        }
    }

    [Fact]
    public async Task Live_todo_turn_with_files_does_not_restart_the_answer()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        // The 2026-06-06 user repro: "using todo, generate three random files".
        // qwen narrates AND calls set_todos in the same round; before the runner
        // echoed that narration back, the model saw a done-marked list with no
        // files in its own (empty) turn, concluded it had skipped the work, and
        // restarted the answer every round ("oops, I jumped the gun" ×3, files
        // generated twice). With the narration echoed, each file is produced
        // exactly once.
        var todo = new TodoTool();
        var tools = ToolSpecs(todo);

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = SystemPrompts.Canvas },
            new() { Role = "user", Content = "using todo, generate three random files for me." },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var (answer, todoCalls, log) = await RunTodoLoopAsync(client!, messages, tools, todo, cts.Token);

        todoCalls.Should().NotBeEmpty("the user explicitly asked for the todo list");
        answer.Should().NotBeEmpty();

        // The restart bug's fingerprint is duplicated files and an abandoned
        // list — so assert the turn is COHERENT: each emitted file appears
        // exactly once, and the last accepted set_todos call finished the plan.
        // (Exactly-3-artifacts would be at the mercy of fence typos the strict
        // extractor rightly ignores, e.g. "name/notes.md" — sampling noise,
        // not a loop defect.)
        var artifacts = ArtifactExtractor.Extract(answer);
        artifacts.Should().NotBeEmpty($"files were requested (rounds: {log}; got: {answer})");
        artifacts.Select(a => a.Name).Should().OnlyHaveUniqueItems(
            $"a coherent turn emits each file once (rounds: {log}; got: {answer})");
        todoCalls[^1].ArgumentsJson.Should().NotContainAny(["pending", "active"],
            $"the turn must end with the plan completed, not abandoned (rounds: {log})");
    }

    /// <summary>Offer a single tool the way TurnPlanner's ToSpec does.</summary>
    private static List<ChatToolSpec> ToolSpecs(ITool tool) =>
        [new() { Name = tool.Name, Description = tool.Description, ParametersSchema = tool.ParametersSchema }];

    /// <summary>
    /// Drive the model through the tool loop exactly as TurnRunner does: stream
    /// a round; on tool calls append ONE assistant message carrying the round's
    /// calls (content omitted) plus one tool-result message per call, executing
    /// the REAL TodoTool; stop when a round ends with no calls (the answer).
    /// </summary>
    private static async Task<(string Answer, List<ChatModelToolCall> TodoCalls, string Log)> RunTodoLoopAsync(
        VllmChatModelClient client,
        List<ChatModelMessage> messages,
        List<ChatToolSpec> tools,
        TodoTool todo,
        CancellationToken token)
    {
        var todoCalls = new List<ChatModelToolCall>();
        var answer = new System.Text.StringBuilder();
        var log = new System.Text.StringBuilder();

        // TurnRunner allows 5 tool rounds plus the final answer round; a model
        // pacing itself one step per round (work → set_todos → work) needs them.
        for (var round = 0; round < 6; round++)
        {
            // Thinking is off, so use the card's INSTRUCT sampling (what the
            // planner now applies): temp 0.7 / top_p 0.8 / presence 1.5.
            // Greedy (temp 0) decoding is explicitly advised against — it sent
            // qwen into "ask you to ask you to…" repetition loops here. The
            // fixed seed keeps runs comparable.
            var request = new ChatCompletionRequest
            {
                ModelId = "default",
                Messages = messages,
                Tools = tools,
                Temperature = 0.7,
                TopP = 0.8,
                PresencePenalty = 1.5,
                Seed = 42,
                MaxTokens = 2200,
                EnableThinking = false,
            };

            var roundText = new System.Text.StringBuilder();
            var roundCalls = new List<ChatModelToolCall>();
            await foreach (var chunk in client.StreamAsync(request, token))
            {
                roundText.Append(chunk.TextDelta);
                if (chunk.ToolCall is not null)
                {
                    roundCalls.Add(chunk.ToolCall);
                }
            }

            // TurnRunner accumulates content across rounds — a model may stream
            // text AND tool calls in the same round, and that text is part of
            // the answer.
            answer.Append(roundText);
            log.Append($"[round {round}: text {roundText.Length} chars, calls: ")
                .AppendJoin("; ", roundCalls.Select(c => $"{c.Name}({c.ArgumentsJson})"))
                .Append("]\n");

            if (roundCalls.Count == 0)
            {
                return (answer.ToString(), todoCalls, log.ToString());
            }

            // Mirror TurnRunner: the round's narration rides back with the calls,
            // so the model sees its own words next round.
            messages.Add(new ChatModelMessage
            {
                Role = "assistant",
                Content = roundText.Length > 0 ? roundText.ToString() : null,
                ToolCalls = roundCalls,
            });
            foreach (var call in roundCalls)
            {
                call.Name.Should().Be("set_todos", "the todo tool is the only one offered");
                var outcome = await todo.ExecuteAsync(
                    new ToolInvocation { Pid = "default", ArgumentsJson = call.ArgumentsJson }, token);

                // Mirror production: a failed call feeds {"error":…} back and
                // the model retries next round — bad args are not a dead turn.
                if (outcome.Success)
                {
                    todoCalls.Add(call);
                }

                messages.Add(new ChatModelMessage
                {
                    Role = "tool",
                    Content = outcome.Success
                        ? outcome.ResultJson ?? string.Empty
                        : System.Text.Json.JsonSerializer.Serialize(new { error = outcome.Error }),
                    ToolCallId = call.Id,
                });
            }
        }

        return (answer.ToString(), todoCalls, log.ToString());
    }

    [Fact]
    public async Task Live_model_streams_reasoning_when_thinking_is_on()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set — live vLLM integration skipped.");

        var request = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages =
            [
                new ChatModelMessage { Role = "user", Content = "What is 17 * 23? Answer briefly." },
            ],
            Temperature = 0,
            MaxTokens = 700,
            EnableThinking = true,
        };

        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await foreach (var chunk in client!.StreamAsync(request, cts.Token))
        {
            content.Append(chunk.TextDelta);
            reasoning.Append(chunk.ReasoningDelta);
        }

        // --reasoning-parser qwen3 splits thinking from the answer; both flow
        // through our parser into their own delta lanes.
        reasoning.Length.Should().BeGreaterThan(0, "thinking-on must surface reasoning_content");
        content.ToString().Should().Contain("391");
    }
}
