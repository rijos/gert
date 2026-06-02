using FluentValidation;
using Gert.Service;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Builds the real <see cref="AddGertServices"/> container so validation tests
/// exercise the <b>production</b> registrations — the same provider, the same
/// validators, the same fail-closed wiring the API and Console get. A test that
/// resolves a validator here is asserting against what actually ships.
/// </summary>
internal static class ValidationTestHost
{
    /// <summary>
    /// A container with the full Gert service layer registered plus a
    /// <see cref="ToolRegistry"/> over the given tool ids (so tool-toggle rules
    /// have a known set to validate against).
    /// </summary>
    public static ServiceProvider Build(params string[] toolIds)
    {
        var services = new ServiceCollection();

        // Register the registry BEFORE AddGertServices so its TryAdd fallback yields.
        services.AddSingleton(new ToolRegistry(toolIds.Select(id => (ITool)new StubTool(id))));
        services.AddGertServices();

        return services.BuildServiceProvider();
    }

    /// <summary>Resolve a concrete validator from the production registrations.</summary>
    public static IValidator<T> Validator<T>(this IServiceProvider sp) =>
        sp.GetRequiredService<IValidator<T>>();

    private sealed class StubTool : ITool
    {
        public StubTool(string id) => Id = id;

        public string Id { get; }

        public string Name => Id;

        public string Description => Id;

        public string ParametersSchema => "{}";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true });
    }
}
