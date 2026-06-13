using Gert.Service;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// A fixed <see cref="IUserContext"/> for the ingestion integration tests - the service
/// layer's only view of identity. Defaults to the same (iss, sub) the rag tests
/// use so the resolved folder matches <see cref="ProviderFixture"/>.
/// </summary>
internal sealed class FixedUserContext : IUserContext
{
    public string Sub { get; init; } = "u7d-sub";

    public string Iss { get; init; } = ProviderFixture.ExpectedIssuer;

    public string Username { get; init; } = "tester";

    public bool IsAdmin { get; init; }

    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>();

    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
