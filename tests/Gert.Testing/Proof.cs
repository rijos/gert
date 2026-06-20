using Gert.Service;
using Gert.Tools.Builtin;
using Gert.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Testing;

/// <summary>
/// Test-side construction of <see cref="Validated{T}"/> proofs. The proof type's
/// constructor is private by design - there is no bypass, in tests or
/// in production - so a test obtains a proof the same way a controller does: by
/// validating through the real provider. This builds the production registrations once
/// (<see cref="ServiceCollectionExtensions.AddGertServices"/> plus <c>AddBuiltinTools</c>, which
/// supplies the id-only <c>ToolRegistry</c> the tool-toggle validator needs), so a proof minted
/// here passes exactly the validators that ship. An invalid value throws
/// <see cref="ValidationException"/>, which is what the service-boundary tests want to assert against.
/// </summary>
public static class Proof
{
    private static readonly IValidationProvider ValidationProvider =
        new ServiceCollection()
            .AddGertServices()
            .AddBuiltinTools()
            .BuildServiceProvider()
            .GetRequiredService<IValidationProvider>();

    /// <summary>
    /// The production-wired validation provider. Exposed so a test that constructs a
    /// typed-args tool (<c>ToolCall&lt;TArgs, _&gt;</c>) directly can inject the REAL
    /// provider its ctor now takes - the same validators that ship, so the tool's
    /// fail-closed arg check is exercised, not stubbed away.
    /// </summary>
    public static IValidationProvider Validation => ValidationProvider;

    /// <summary>Validate <paramref name="value"/> and return its proof (throws on failure).</summary>
    public static Validated<T> Of<T>(T value) => ValidationProvider.Prove(value);
}
