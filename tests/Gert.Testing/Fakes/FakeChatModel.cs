using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Gert.Testing.Fakes;

/// <summary>
/// Microsoft.Extensions.AI <see cref="IChatClient"/> double (testing.md section 4.1, A.3).
/// Resolves a reply from <c>fixtures.json</c> by the trimmed last user message
/// (exact/contains per fixture) and <b>streams</b> it as <see cref="ChatResponseUpdate"/>s -
/// text as <see cref="TextContent"/>, thinking as <see cref="TextReasoningContent"/>, a tool call
/// as a completed <see cref="FunctionCallContent"/> (non-null arguments), and usage as a
/// <see cref="UsageContent"/> on the terminal update.
///
/// Tool loop: when a matched fixture has a <c>tool_call</c>, the first call (the
/// messages carry no tool result yet) streams the tool call and finishes
/// <c>tool_calls</c>; the follow-up call (the messages now include a <c>tool</c>
/// role result) replays <c>after_tool.deltas</c>. With no match, the
/// <c>echo</c> fallback streams <c>Echo: &lt;message&gt;</c> tokenised on word
/// boundaries (spaces preserved) so SSE framing and the typewriter caret have
/// real chunks to render. Only streaming is supported (the loop never calls
/// <see cref="GetResponseAsync"/>).
/// </summary>
public sealed class FakeChatModel : IChatClient
{
    private readonly Fixtures _fixtures;

    /// <summary>Build over the canonical shared fixtures.</summary>
    public FakeChatModel()
        : this(Fixtures.Load())
    {
    }

    /// <summary>Build over explicit fixtures (tests may inject their own).</summary>
    public FakeChatModel(Fixtures fixtures)
    {
        _fixtures = fixtures ?? throw new ArgumentNullException(nameof(fixtures));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var lastUser = LastUserMessage(list);
        var hasToolResult = list.Any(m => m.Role == ChatRole.Tool);

        // Rough context estimate so the SPA's ring lights up under serve-mock
        // (~ chars/4, the classic token heuristic).
        var promptTokens = list.Sum(m =>
            m.Text.Length + m.Contents.OfType<TextReasoningContent>().Sum(r => r.Text.Length)) / 4;

        var fixture = Resolve(lastUser);

        if (fixture is not null && fixture.ToolCall is not null && !hasToolResult)
        {
            foreach (var thought in fixture.ReasoningDeltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Reasoning(thought);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return Call($"call_{fixture.ToolCall.Name}_1", fixture.ToolCall.Name, fixture.ToolCall.Arguments);
            yield return Finish(
                string.IsNullOrEmpty(fixture.Finish) ? "tool_calls" : fixture.Finish,
                completionTokens: null,
                promptTokens);
            yield break;
        }

        if (fixture is not null && fixture.ToolCall is not null && hasToolResult)
        {
            var after = fixture.AfterTool;
            foreach (var thought in after?.ReasoningDeltas ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Reasoning(thought);
            }

            foreach (var delta in after?.Deltas ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Text(delta);
            }

            yield return Finish(after?.Finish ?? "stop", after?.Usage?.CompletionTokens, promptTokens);
            yield break;
        }

        if (fixture is not null)
        {
            foreach (var thought in fixture.ReasoningDeltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Reasoning(thought);
            }

            foreach (var delta in fixture.Deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Text(delta);
            }

            yield return Finish(
                string.IsNullOrEmpty(fixture.Finish) ? "stop" : fixture.Finish,
                fixture.Usage?.CompletionTokens,
                promptTokens);
            yield break;
        }

        foreach (var chunk in EchoChunks(lastUser))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Text(chunk);
        }

        yield return Finish("stop", completionTokens: null, promptTokens);
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("FakeChatModel only supports streaming (the agent loop streams).");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Reasoning(string text) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new TextReasoningContent(text)],
    };

    private static ChatResponseUpdate Call(string id, string name, string argumentsJson) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name, ParseArguments(argumentsJson))],
    };

    private static ChatResponseUpdate Finish(string finishReason, int? completionTokens, int promptTokens) => new()
    {
        Role = ChatRole.Assistant,
        FinishReason = new ChatFinishReason(finishReason),
        Contents = [new UsageContent(new UsageDetails
        {
            InputTokenCount = promptTokens,
            OutputTokenCount = completionTokens,
        })],
    };

    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                   ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private CompletionFixture? Resolve(string lastUser)
    {
        var trimmed = lastUser.Trim();
        foreach (var c in _fixtures.Completions)
        {
            var when = c.When.Trim();
            var isMatch = string.Equals(c.Match, "contains", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Contains(when, StringComparison.OrdinalIgnoreCase)
                : string.Equals(trimmed, when, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                return c;
            }
        }

        return null;
    }

    private static string LastUserMessage(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return messages[i].Text;
            }
        }

        return string.Empty;
    }

    /// <summary>"Echo: " + the message, split on word boundaries with spaces preserved.</summary>
    private static IEnumerable<string> EchoChunks(string message)
    {
        yield return "Echo: ";

        var start = 0;
        for (var i = 0; i < message.Length; i++)
        {
            if (message[i] == ' ')
            {
                // Emit the word plus its trailing space as one chunk.
                yield return message[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < message.Length)
        {
            yield return message[start..];
        }
    }
}
