using Gert.Tools;
using Gert.Tools.Hosting;

namespace Gert.Testing.Fakes;

/// <summary>
/// A scriptable <see cref="IToolDelegate"/> for tool tests: captures the
/// <see cref="DelegateRequest"/> it receives and returns a settable
/// <see cref="Result"/>. Tests exercising <c>run_sub_agent</c> set this on a
/// <see cref="FakeToolHost"/> so the tool's arg-parsing + result-shaping is tested
/// without the real nested loop (that is ChatToolDelegate's own test).
/// </summary>
public sealed class FakeToolDelegate : IToolDelegate
{
    /// <summary>The request the tool passed - asserted by tests.</summary>
    public DelegateRequest? LastRequest { get; private set; }

    /// <summary>The result returned to the tool; defaults to a one-round success.</summary>
    public DelegateResult Result { get; set; } =
        new() { Success = true, Text = "delegated result", Rounds = 1 };

    public Task<DelegateResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(Result);
    }
}
