using Gert.Model;
using Gert.Model.Tools;

namespace Gert.Tools.Ports;

/// <summary>
/// Port for the code sandbox (chat-and-tools.md section sandbox; security F5). Two real
/// backends live in <c>Gert.Tools.Builtin</c>, selected by <c>Gert:Tools:Sandbox:Type</c>:
/// <c>monty</c> (the default - Pydantic's syscall-free Rust Python interpreter via
/// a sidecar) and <c>gvisor</c> (an ephemeral <c>runsc</c> container). Both run
/// with egress off by default and no <c>/data</c> mount; tests use a stub. Only
/// captured stdout/stderr returns.
/// </summary>
public interface IPythonSandbox
{
    /// <summary>Execute Python in an ephemeral sandbox and capture its output.</summary>
    Task<PythonSandboxResult> RunPythonAsync(
        string code,
        CancellationToken cancellationToken = default);
}
