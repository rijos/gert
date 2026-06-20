namespace Gert.Tools;

/// <summary>
/// Which pre-scoped object store a tool addresses. The host translates this to the identity-bearing
/// storage scope (under the token-derived user folder) - the tool never supplies iss/sub/pid.
/// </summary>
public enum ResourceScope
{
    /// <summary>Project-wide objects (memory, files) shared across the project's conversations.</summary>
    Project,

    /// <summary>Objects scoped to the active conversation (canvas artifacts).</summary>
    Chat,
}
