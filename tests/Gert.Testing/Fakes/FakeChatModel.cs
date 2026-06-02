using System.Runtime.CompilerServices;
using Gert.Service.External;

namespace Gert.Testing.Fakes;

/// <summary>
/// OpenAI-compatible <see cref="IChatModelClient"/> double (testing.md §4.1, A.3).
/// Resolves a reply from <c>fixtures.json</c> by the trimmed last user message
/// (exact/contains per fixture) and <b>streams</b> it as <see cref="ChatModelChunk"/>s.
///
/// Tool loop: when a matched fixture has a <c>tool_call</c>, the first call (the
/// messages carry no tool result yet) streams the tool call and finishes
/// <c>tool_calls</c>; the follow-up call (the messages now include a <c>tool</c>
/// role result) replays <c>after_tool.deltas</c>. With no match, the
/// <c>echo</c> fallback streams <c>Echo: &lt;message&gt;</c> tokenised on word
/// boundaries (spaces preserved) so SSE framing and the typewriter caret have
/// real chunks to render.
/// </summary>
public sealed class FakeChatModel : IChatModelClient
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
    public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lastUser = LastUserMessage(request.Messages);
        var hasToolResult = request.Messages.Any(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase));

        var fixture = Resolve(lastUser);

        if (fixture is not null && fixture.ToolCall is not null && !hasToolResult)
        {
            // First call: emit the scripted tool call, finish_reason = tool_calls.
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatModelChunk
            {
                ToolCall = new ChatModelToolCall
                {
                    Id = $"call_{fixture.ToolCall.Name}_1",
                    Name = fixture.ToolCall.Name,
                    ArgumentsJson = fixture.ToolCall.Arguments,
                },
            };

            yield return new ChatModelChunk
            {
                FinishReason = string.IsNullOrEmpty(fixture.Finish) ? "tool_calls" : fixture.Finish,
            };
            yield break;
        }

        if (fixture is not null && fixture.ToolCall is not null && hasToolResult)
        {
            // Follow-up call: replay after_tool.
            var after = fixture.AfterTool;
            var deltas = after?.Deltas ?? [];
            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatModelChunk { TextDelta = delta };
            }

            yield return new ChatModelChunk
            {
                FinishReason = after?.Finish ?? "stop",
                TokenCount = after?.Usage?.CompletionTokens,
            };
            yield break;
        }

        if (fixture is not null)
        {
            // Plain completion: stream the deltas.
            foreach (var delta in fixture.Deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatModelChunk { TextDelta = delta };
            }

            yield return new ChatModelChunk
            {
                FinishReason = string.IsNullOrEmpty(fixture.Finish) ? "stop" : fixture.Finish,
                TokenCount = fixture.Usage?.CompletionTokens,
            };
            yield break;
        }

        // Fallback: echo the last user message, tokenised on word boundaries.
        foreach (var chunk in EchoChunks(lastUser))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatModelChunk { TextDelta = chunk };
        }

        yield return new ChatModelChunk { FinishReason = "stop" };
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

    private static string LastUserMessage(IReadOnlyList<ChatModelMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return messages[i].Content;
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
