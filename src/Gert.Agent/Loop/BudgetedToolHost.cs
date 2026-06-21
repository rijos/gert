using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Ui;

namespace Gert.Agent.Loop;

/// <summary>
/// Wraps the run's <see cref="IToolHost"/> for ONE tool call, forwarding Resources/Ui/Delegate
/// unchanged but replacing <see cref="ToolLimits.TokenBudget"/> with the resolved tool's effective
/// <c>ToolBounds.TokenBudget</c> (the turn-wide <see cref="ToolLimits.Deadline"/> is preserved). This
/// feeds the existing, unconsumed token-budget seam - no new enforcement; turn-budgets.md keeps token
/// budgets open design, but a tool that wants to honour its allowance now reads it here.
/// </summary>
internal sealed class BudgetedToolHost : IToolHost
{
    private readonly IToolHost _inner;

    public BudgetedToolHost(IToolHost inner, int tokenBudget)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Limits = new ToolLimits(inner.Limits.Deadline, tokenBudget);
    }

    public IToolResources Resources => _inner.Resources;

    public IToolUi? Ui => _inner.Ui;

    public IToolDelegate Delegate => _inner.Delegate;

    public ToolLimits Limits { get; }
}
