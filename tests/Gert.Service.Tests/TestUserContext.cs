using Gert.Service;

namespace Gert.Service.Tests;

/// <summary>
/// A fixed <see cref="IUserContext"/> for service tests — the service layer's
/// only view of identity. Tests assert the service threads <see cref="Iss"/> /
/// <see cref="Sub"/> from here (never a caller-supplied user) into the
/// database provider.
/// </summary>
internal sealed class TestUserContext : IUserContext
{
    public string Sub { get; init; } = "sub-123";

    public string Iss { get; init; } = "https://idp.example";

    public string Username { get; init; } = "tester";

    public bool IsAdmin { get; init; }

    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>();

    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
