using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// LIVE integration against a real vLLM server - skipped unless
/// <c>GERT_VLLM_URL</c> is set (CI has no GPU box). Drives the REAL wire path:
/// <see cref="OpenAIChatRequestBuilder"/> -> HTTP SSE -> <see cref="OpenAIStreamParser"/>,
/// then <see cref="ArtifactExtractor"/> over the assembled completion - proving
/// the model can be steered into named html/py/md fences and that our
/// request-shape (chat_template_kwargs, usage) holds against the deployed build.
///
/// Run:  GERT_VLLM_URL=http://vllm-host:8000 [GERT_VLLM_MODEL=qwen36] dotnet test \
///       --filter FullyQualifiedName~OpenAILiveIntegration
/// </summary>
public sealed class OpenAILiveIntegrationTests
{
    // The canvas/artifact nudge these live tests feed the model. Production reads
    // it from Gert:Prompts:Canvas; the test carries its own realistic copy (it
    // must mention the make/edit artifact tools to steer the model the same way).
    private const string CanvasPrompt =
        "When you produce a complete, self-contained file (an HTML page, a script, a Markdown " +
        "document, an SVG, etc.), call the make_artifact tool with the whole file content - it " +
        "opens in the user's canvas. Do not paste a whole file into a code block. To change an " +
        "existing artifact, use edit_artifact to replace just the part that changes rather than " +
        "remaking the whole file; use read_artifact to see its current content first if needed. " +
        "Keep ordinary code blocks for short inline snippets and examples.";

    private static OpenAIChatModelClient? CreateClient(out string baseUrl, bool thinking = false)
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

        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(180),
        };

        // Sampling + the thinking template kwarg ride the provider: the qwen36
        // instruct vs thinking presets, applied to every request this client sends.
        // Tool round-trips run instruct; the reasoning test passes thinking: true.
        var parameters = new ChatProviderParameters
        {
            BaseUrl = baseUrl,
            Model = Environment.GetEnvironmentVariable("GERT_VLLM_MODEL") ?? "qwen36",
            Temperature = thinking ? 0.6 : 0.7,
            TopP = thinking ? 0.95 : 0.8,
            PresencePenalty = thinking ? null : 1.5,
            Extra = new()
            {
                ["top_k"] = "20",
                ["chat_template_kwargs.enable_thinking"] = thinking ? "true" : "false",
            },
        };

        return new OpenAIChatModelClient(
            http, parameters, NullLogger<OpenAIChatModelClient>.Instance);
    }

    [Fact]
    public async Task Live_model_creates_then_edits_a_markdown_artifact_via_the_canvas_tools()
    {
        var client = CreateClient(out var baseUrl);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        // The screenshot regression, solved at the root: a NATURAL "make a file"
        // request, with ONLY the built-in system prompt + the tool schemas (exactly
        // what TurnPlanner sends). The model must call make_artifact rather than
        // pasting a fence - and a Markdown file with its OWN ``` code block must
        // survive intact, because the content is a JSON arg, not regex-extracted.
        var host = new FakeToolHost();
        var (specs, toolsByName) = ArtifactToolset();

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = CanvasPrompt },
            new()
            {
                Role = "user",
                Content =
                    "Create a Markdown file called notes.md: a one-line intro, then a Python " +
                    "code block showing a hello-world, then a closing line.",
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var calls = await DriveToolLoopAsync(client!, messages, specs, toolsByName, host, cts.Token);

        // The file reached the canvas through the tool - not a fenced block - and
        // the nested ``` python fence is intact inside it (the truncation bug).
        calls.Should().Contain(
            c => c.Name == "make_artifact",
            $"the model must create the file with the tool (calls: {string.Join(",", calls.Select(c => c.Name))})");
        var md = (await host.ObjectStore.GetAsync(ResourceScope.Chat, "notes.md"))
            .Should().NotBeNull().And.Subject as StoredObject;
        md!.Content.Should().Contain("```", "a Markdown file's own code fence rides inside the JSON arg untouched");

        // Iterate: a follow-up turn must MODIFY the file (edit or remake), not
        // restate it - and the change must land in the stored artifact.
        messages.Add(new ChatModelMessage
        {
            Role = "user",
            Content = "Add a new section heading '## Setup' at the end of notes.md.",
        });
        var followUp = await DriveToolLoopAsync(client!, messages, specs, toolsByName, host, cts.Token);

        followUp.Should().Contain(
            c => c.Name == "edit_artifact" || c.Name == "make_artifact",
            $"the model must change the file with a tool (calls: {string.Join(",", followUp.Select(c => c.Name))})");
        (await host.ObjectStore.GetAsync(ResourceScope.Chat, "notes.md"))!
            .Content.Should().Contain("Setup", $"the edit must land (got: {baseUrl})");
    }

    [Fact]
    public async Task Live_tool_round_trip_with_thinking_disabled_completes()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        // The clock-tool crash (2026-06-06): with enable_thinking=false the
        // qwen3 tool parser on vLLM 0.22 streams a lone, unterminated "{" for a
        // no-argument get_datetime call. Unnormalized, that fragment fails the
        // tool AND 400s the second round when echoed back inside the assistant
        // tool_calls message. This drives the REAL wire path through both
        // rounds: the parsed arguments must be valid JSON, and the follow-up
        // request carrying them must complete.
        var clock = new ClockTool(Gert.Testing.Proof.Validation, TimeProvider.System);
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
            MaxTokens = 400,
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
        // the tool loop does - the request must not 400.
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
            MaxTokens = 400,
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
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        // The planning contract: a natural multi-step request plus the bare
        // tool description must steer the model into set_todos, and the REAL
        // TodoTool must accept whatever arguments the parser assembled. This
        // is the within-turn half of the todo story (the cross-turn half is
        // the reminder test below).
        var todo = new TodoTool(Gert.Testing.Proof.Validation);
        var tools = ToolSpecs(todo);

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = CanvasPrompt },
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
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        // The cross-turn gap: TurnPlanner rebuilds history as role+content only,
        // so the todo list set via tool calls in turn 1 is INVISIBLE in turn 2.
        // This renders turn 2 exactly as the planner would - assistant content
        // without its tool_calls - and appends TodoTool.CrossTurnReminder to the
        // new user message. The assistant content is deliberately vague about
        // which steps are done: ONLY the reminder says winter is the one left,
        // so a winter haiku in the answer proves the snapshot was read.
        var todo = new TodoTool(Gert.Testing.Proof.Validation);
        var tools = ToolSpecs(todo);

        const string snapshot =
            """{"todos":[{"text":"write the spring haiku","status":"done"},{"text":"write the summer haiku","status":"done"},{"text":"write the winter haiku","status":"pending"}]}""";

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = CanvasPrompt },
            new()
            {
                Role = "user",
                Content =
                    "Write three haiku - spring, summer, winter - one at a time, and track " +
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
                Content = "continue\n\n" + TodoTool.CrossTurnReminder(snapshot),
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var (answer, todoCalls, log) = await RunTodoLoopAsync(client!, messages, tools, todo, cts.Token);

        // The reminder is the only place winter is marked as the remaining step.
        answer.Should().NotBeEmpty("the turn must end in a final answer");
        answer.Should().ContainEquivalentOf("winter", "the pending item lives only in the reminder snapshot");

        // If the model also updated its list (the reminder asks it to), the new
        // list must be a continuation of the snapshot - not a fresh start.
        if (todoCalls.Count > 0)
        {
            todoCalls[^1].ArgumentsJson.Should().ContainEquivalentOf(
                "winter",
                "an updated list continues the snapshot rather than replacing the plan");
        }
    }

    [Fact]
    public async Task Live_todo_turn_with_files_does_not_restart_the_answer()
    {
        var client = CreateClient(out _);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        // The 2026-06-06 user repro: "using todo, generate three random files".
        // qwen narrates AND calls set_todos in the same round; before the runner
        // echoed that narration back, the model saw a done-marked list with no
        // files in its own (empty) turn, concluded it had skipped the work, and
        // restarted the answer every round ("oops, I jumped the gun" x3, files
        // generated twice). With the narration echoed, each file is produced once.
        // Files now flow through make_artifact, so the todo tool and the artifact
        // tools are offered together and the loop executes both for real.
        var host = new FakeToolHost();
        var (artifactSpecs, artifactTools) = ArtifactToolset();
        var todo = new TodoTool(Gert.Testing.Proof.Validation);

        var specs = ToolSpecs(todo).Concat(artifactSpecs).ToList();
        var toolsByName = new Dictionary<string, ITool>(artifactTools, StringComparer.Ordinal)
        {
            [todo.Name] = todo,
        };

        var messages = new List<ChatModelMessage>
        {
            new() { Role = "system", Content = CanvasPrompt },
            new() { Role = "user", Content = "using todo, generate three random files for me." },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var calls = await DriveToolLoopAsync(client!, messages, specs, toolsByName, host, cts.Token);

        var todoCalls = calls.Where(c => c.Name == "set_todos").ToList();
        todoCalls.Should().NotBeEmpty("the user explicitly asked for the todo list");

        // The restart bug's fingerprint is duplicated files and an abandoned list -
        // so assert the turn is COHERENT: files were created via the tool, each name
        // is unique (no double-generation), and the last set_todos finished the plan.
        var files = await host.ObjectStore.ListAsync(ResourceScope.Chat);
        files.Should().NotBeEmpty($"files were requested (calls: {string.Join(",", calls.Select(c => c.Name))})");
        files.Select(a => a.Name).Should().OnlyHaveUniqueItems(
            "a coherent turn emits each file once");
        todoCalls[^1].ArgumentsJson.Should().NotContainAny(
            ["pending", "active"],
            "the turn must end with the plan completed, not abandoned");
    }

    /// <summary>Offer a single tool the way TurnPlanner's ToSpec does.</summary>
    private static List<ChatToolSpec> ToolSpecs(ITool tool) =>
        [new() { Name = tool.Name, Description = tool.Description, ParametersSchema = tool.ParametersSchema }];

    /// <summary>The canvas artifact tools (host-driven storage), with their specs.</summary>
    private static (List<ChatToolSpec> Specs, Dictionary<string, ITool> ByName) ArtifactToolset()
    {
        ITool[] tools =
        [
            new MakeArtifactTool(Gert.Testing.Proof.Validation),
            new EditArtifactTool(Gert.Testing.Proof.Validation),
            new ReadArtifactTool(Gert.Testing.Proof.Validation),
        ];
        var specs = tools.Select(t => new ChatToolSpec
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.ParametersSchema,
        }).ToList();
        return (specs, tools.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// Drive the model through the tool loop exactly as TurnRunner does, but for an
    /// arbitrary tool set: stream a round; execute each call against the real tool
    /// (handing it the pre-scoped <paramref name="host"/> the artifact tools read for
    /// their object store); echo the assistant tool-call message + one tool result per
    /// call; stop when a round makes no calls. Returns every tool call seen across rounds.
    /// </summary>
    private static async Task<List<ChatModelToolCall>> DriveToolLoopAsync(
        OpenAIChatModelClient client,
        List<ChatModelMessage> messages,
        IReadOnlyList<ChatToolSpec> specs,
        IReadOnlyDictionary<string, ITool> toolsByName,
        FakeToolHost host,
        CancellationToken token)
    {
        var allCalls = new List<ChatModelToolCall>();

        for (var round = 0; round < 6; round++)
        {
            var request = new ChatCompletionRequest
            {
                ModelId = "default",
                Messages = messages,
                Tools = specs.ToList(),
                MaxTokens = 2200,
            };

            var text = new System.Text.StringBuilder();
            var calls = new List<ChatModelToolCall>();
            await foreach (var chunk in client.StreamAsync(request, token))
            {
                text.Append(chunk.TextDelta);
                if (chunk.ToolCall is not null)
                {
                    calls.Add(chunk.ToolCall);
                }
            }

            if (calls.Count == 0)
            {
                break;
            }

            allCalls.AddRange(calls);
            messages.Add(new ChatModelMessage
            {
                Role = "assistant",
                Content = text.Length > 0 ? text.ToString() : null,
                ToolCalls = calls,
            });

            foreach (var call in calls)
            {
                string content;
                if (toolsByName.TryGetValue(call.Name, out var tool))
                {
                    var outcome = await tool.ExecuteAsync(
                        new ToolInvocation
                        {
                            Pid = "default",
                            ArgumentsJson = call.ArgumentsJson,
                            ConversationId = "live-conv",
                            MessageId = "live-msg",
                        }, host, token);
                    content = outcome.Success
                        ? outcome.ResultJson ?? "{}"
                        : System.Text.Json.JsonSerializer.Serialize(new { error = outcome.Error });
                }
                else
                {
                    content = System.Text.Json.JsonSerializer.Serialize(new { error = $"no tool named '{call.Name}'" });
                }

                messages.Add(new ChatModelMessage { Role = "tool", Content = content, ToolCallId = call.Id });
            }
        }

        return allCalls;
    }

    /// <summary>
    /// Drive the model through the tool loop exactly as TurnRunner does: stream
    /// a round; on tool calls append ONE assistant message carrying the round's
    /// calls (content omitted) plus one tool-result message per call, executing
    /// the REAL TodoTool; stop when a round ends with no calls (the answer).
    /// </summary>
    private static async Task<(string Answer, List<ChatModelToolCall> TodoCalls, string Log)> RunTodoLoopAsync(
        OpenAIChatModelClient client,
        List<ChatModelMessage> messages,
        List<ChatToolSpec> tools,
        TodoTool todo,
        CancellationToken token)
    {
        var todoCalls = new List<ChatModelToolCall>();
        var answer = new System.Text.StringBuilder();
        var log = new System.Text.StringBuilder();

        // TurnRunner allows 5 tool rounds plus the final answer round; a model
        // pacing itself one step per round (work -> set_todos -> work) needs them.
        for (var round = 0; round < 6; round++)
        {
            // Thinking is off, so use the card's INSTRUCT sampling (what the
            // planner now applies): temp 0.7 / top_p 0.8 / presence 1.5.
            // Greedy (temp 0) decoding is explicitly advised against - it sent
            // qwen into "ask you to ask you to..." repetition loops here. The
            // fixed seed keeps runs comparable.
            var request = new ChatCompletionRequest
            {
                ModelId = "default",
                Messages = messages,
                Tools = tools,
                MaxTokens = 2200,
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

            // TurnRunner accumulates content across rounds - a model may stream
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

                // Mirror production: a failed call feeds {"error":...} back and
                // the model retries next round - bad args are not a dead turn.
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
        var client = CreateClient(out _, thinking: true);
        Assert.SkipWhen(client is null, "GERT_VLLM_URL is not set - live vLLM integration skipped.");

        var request = new ChatCompletionRequest
        {
            ModelId = "default",
            Messages =
            [
                new ChatModelMessage { Role = "user", Content = "What is 17 * 23? Answer briefly." },
            ],
            MaxTokens = 700,
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
