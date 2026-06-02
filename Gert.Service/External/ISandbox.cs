namespace Gert.Service.External;

/// <summary>
/// Port for the code sandbox (chat-and-tools.md § sandbox). The real client
/// (ephemeral gVisor <c>runsc</c> container, egress off by default, no <c>/data</c>
/// mount, security F5) lives in <c>Gert.External</c> (U10); tests use a stub. Only
/// captured stdout/stderr returns.
/// </summary>
public interface ISandbox
{
    /// <summary>Execute Python in an ephemeral sandbox and capture its output.</summary>
    Task<SandboxResult> RunPythonAsync(
        string code,
        CancellationToken cancellationToken = default);
}
