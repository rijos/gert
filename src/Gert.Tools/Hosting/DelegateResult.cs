namespace Gert.Tools.Hosting;

/// <summary>
/// The outcome of a delegated task (<see cref="IToolDelegate.RunAsync"/>): the sub-agent's final
/// <see cref="Text"/> and the <see cref="Rounds"/> it took on success, or an <see cref="Error"/> the
/// model can read on failure (bad args, caps, non-convergence, ran out of time).
/// </summary>
public sealed record DelegateResult
{
    public required bool Success { get; init; }

    /// <summary>The sub-agent's final answer (the only thing reported back) - set on success.</summary>
    public string? Text { get; init; }

    /// <summary>How many nested rounds the sub-agent ran.</summary>
    public int Rounds { get; init; }

    /// <summary>Model-readable failure reason - set when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
