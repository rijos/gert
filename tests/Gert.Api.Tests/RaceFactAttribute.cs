using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <see cref="FactAttribute"/> for the timing-coupled race/dead-zone tests. Sets
/// <see cref="FactAttribute.Explicit"/> so a bare <c>dotnet test</c> / <c>make test</c>
/// skips them; only <c>make test-race</c> opts in by name via the xUnit native runner
/// (<c>dotnet run -- -explicit only</c>). Pair with class-level
/// <c>[Trait("Category", "Race")]</c> as a second line of defence behind the Makefile's
/// <c>Category!=Race</c> filter.
/// </summary>
public sealed class RaceFactAttribute : FactAttribute
{
    public RaceFactAttribute() => Explicit = true;
}
