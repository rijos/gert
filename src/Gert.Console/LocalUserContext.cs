using Gert.Service;
using Gert.Service.Tools;

namespace Gert.Console;

/// <summary>
/// <see cref="IUserContext"/> for the CLI host: a single, fixed local user. The
/// Console bypasses the API/JWT entirely and drives the <b>same</b> services
/// directly (tech-stack.md § Architecture), so it supplies a stable identity and
/// the blanket tool grant (<c>"*"</c>) — every registered capability id.
/// <para>
/// The identity is fixed: <see cref="Iss"/> <c>gert-console</c> + <see cref="Sub"/>
/// <c>local</c> derive one stable user folder key. The provisioning gate's
/// <c>ExpectedIssuer</c> must be configured to <c>gert-console</c> so the
/// fail-closed <c>iss</c> assertion accepts this identity (security F12).
/// </para>
/// </summary>
public sealed class LocalUserContext : IUserContext
{
    /// <summary>The fixed issuer of the local console identity (the folder anchor with <see cref="Sub"/>).</summary>
    public const string ConsoleIssuer = "gert-console";

    /// <summary>The fixed subject of the local console identity.</summary>
    public const string ConsoleSubject = "local";

    /// <summary>
    /// Build the local user over the tool registry, so <see cref="AllowedTools"/>
    /// is the full set of registered capability ids — the <c>"*"</c> grant
    /// resolved from <see cref="ToolRegistry.AllIds"/> (auth.md § the claim is the
    /// ceiling: the Console's ceiling is "everything the system has").
    /// </summary>
    public LocalUserContext(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        AllowedTools = registry.AllIds;
    }

    /// <inheritdoc />
    public string Sub => ConsoleSubject;

    /// <inheritdoc />
    public string Iss => ConsoleIssuer;

    /// <inheritdoc />
    public string Username => "local";

    /// <inheritdoc />
    public bool IsAdmin => true;

    /// <inheritdoc />
    public IReadOnlySet<string> AllowedTools { get; }

    /// <inheritdoc />
    public bool CanUseTool(string id) => id is not null && AllowedTools.Contains(id);
}
