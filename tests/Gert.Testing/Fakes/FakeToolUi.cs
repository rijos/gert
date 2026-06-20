using Gert.Tools;

namespace Gert.Testing.Fakes;

/// <summary>
/// A scriptable <see cref="IToolUi"/> for tool tests: captures the last
/// <see cref="InteractionRequest"/> passed to <see cref="AskAsync"/> and returns a settable
/// <see cref="InteractionResult"/> (default: Answered=false - the timeout shape). Tests set
/// <c>host.Ui = fakeUi</c>, drive the tool, then assert against <see cref="CapturedRequest"/>.
/// </summary>
public sealed class FakeToolUi : IToolUi
{
    /// <summary>The request from the most recent <see cref="AskAsync"/> call; null until called.</summary>
    public InteractionRequest? CapturedRequest { get; private set; }

    /// <summary>The result <see cref="AskAsync"/> returns. Defaults to the graceful timeout shape.</summary>
    public InteractionResult Result { get; set; } = new() { Answered = false };

    /// <inheritdoc />
    public Task<InteractionResult> AskAsync(InteractionRequest request, CancellationToken cancellationToken = default)
    {
        CapturedRequest = request;
        return Task.FromResult(Result);
    }
}
