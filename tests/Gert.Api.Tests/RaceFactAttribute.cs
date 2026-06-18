using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for the paced, timing-coupled race/dead-zone tests:
/// it sets <see cref="FactAttribute.Explicit"/>, so the test is <b>skipped unless the
/// runner explicitly opts in</b>. A bare <c>dotnet test</c> (and <c>make test</c>) never
/// runs these - they only execute when asked for by name, which the
/// <c>make test-race</c> target does via the xUnit native runner
/// (<c>dotnet run -- -explicit only</c>). Pair with the class-level
/// <c>[Trait("Category", "Race")]</c> so the Makefile's <c>Category!=Race</c> filter
/// keeps excluding them as a second line of defence.
/// </summary>
public sealed class RaceFactAttribute : FactAttribute
{
    public RaceFactAttribute() => Explicit = true;
}
