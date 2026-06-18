using Gert.Service;
using Gert.Service.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Testing;

/// <summary>
/// Test-side construction of <see cref="Validated{T}"/> proofs. The proof type's
/// constructor is private by design - there is no bypass, in tests or
/// in production - so a test obtains a proof the same way a controller does: by
/// validating through the real provider. This builds the production registrations once
/// (<see cref="ServiceCollectionExtensions.AddGertServices"/> self-registers the
/// built-in <c>ToolRegistry</c>), so a proof minted here passes exactly the validators
/// that ship. An invalid value throws <see cref="ValidationException"/>, which is what
/// the service-boundary tests want to assert against.
/// </summary>
public static class Proof
{
    private static readonly IValidationProvider Validation =
        new ServiceCollection()
            .AddGertServices()
            .BuildServiceProvider()
            .GetRequiredService<IValidationProvider>();

    /// <summary>Validate <paramref name="value"/> and return its proof (throws on failure).</summary>
    public static Validated<T> Of<T>(T value) => Validation.Prove(value);
}
