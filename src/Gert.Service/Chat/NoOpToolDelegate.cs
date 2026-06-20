using Gert.Tools;

namespace Gert.Service.Chat;

/// <summary>
/// An <see cref="IToolDelegate"/> that refuses to delegate - the autonomous sub-agent host's
/// Delegate surface. <c>run_sub_agent</c> is never in the delegable set, so this is a backstop:
/// delegation structurally cannot recurse.
/// </summary>
internal sealed class NoOpToolDelegate : IToolDelegate
{
    public Task<DelegateResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DelegateResult { Success = false, Error = "delegation is unavailable here" });
}
