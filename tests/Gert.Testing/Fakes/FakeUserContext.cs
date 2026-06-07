using Gert.Service;

namespace Gert.Testing.Fakes;

/// <summary>A fixed <see cref="IUserContext"/> with a blanket tool grant, for tests.</summary>
public sealed class FakeUserContext : IUserContext
{
    public string Sub => "local";

    public string Iss => "gert-test";

    public string Username => "tester";

    public bool IsAdmin => true;

    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>();

    public bool CanUseTool(string id) => true;
}
