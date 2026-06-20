namespace Gert.Tools.Hosting;

/// <summary>
/// One delegated task for <see cref="IToolDelegate"/>: the self-contained <see cref="Task"/> the
/// sub-agent must complete and optional background <see cref="Context"/>. The sub-agent sees nothing
/// of the parent conversation - these two strings are everything it knows.
/// </summary>
public sealed record DelegateRequest(string Task, string? Context);
