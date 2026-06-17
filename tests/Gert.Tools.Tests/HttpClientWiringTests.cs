using FluentAssertions;
using Gert.Tools.Sandbox;
using Gert.Tools.Sandbox.GVisor;
using Gert.Tools.Sandbox.Monty;
using Gert.Tools.Search;
using Gert.Tools.Search.SearXNG;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// The HTTP timeout/resilience layering registered by <c>AddGertTools</c>
/// (dotnet-style-guide.md section 9): SearXNG = pipeline total inside the client timeout;
/// monty = HTTP backstop strictly above the sandbox wall clock, enforced at startup (only on
/// the monty backend).
/// </summary>
public sealed class HttpClientWiringTests
{
    /// <summary>AddStandardResilienceHandler stores its options as "{clientName}-standard".</summary>
    private static string PipelineName(string clientName) => clientName + "-standard";

    private static ServiceProvider Provider(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // The generic layer + the shipped plugins (the SearXNG named HttpClient and the monty
        // parameters/HttpClient + relation check live in their plugins now).
        services.AddGertTools(configuration);
        services.AddGertSearchSearXNG();
        services.AddGertSandboxMonty(configuration);
        services.AddGertSandboxGVisor(configuration);
        return services.BuildServiceProvider();
    }

    private static HttpStandardResilienceOptions Pipeline(ServiceProvider provider, string clientName) =>
        provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(PipelineName(clientName));

    [Fact]
    public void Searxng_pipeline_total_sits_inside_the_client_timeout()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Tools:Search:SearchTimeoutSeconds"] = "5",
        });

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(SearXngWebSearch.HttpClientName);
        var pipeline = Pipeline(provider, SearXngWebSearch.HttpClientName);

        // SearchTimeoutSeconds is the whole-call budget (Polly total); the client
        // timeout is a backstop 1 s above it.
        pipeline.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(6));
        pipeline.AttemptTimeout.Timeout.Should().BeLessThanOrEqualTo(pipeline.TotalRequestTimeout.Timeout);
    }

    [Fact]
    public void Monty_defaults_satisfy_the_wall_clock_relation()
    {
        using var provider = Provider();

        var monty = provider.GetRequiredService<IOptions<MontyParameters>>().Value;

        monty.RequestTimeoutSeconds.Should().BeGreaterThan(
            provider.GetRequiredService<IOptions<PythonSandboxOptions>>().Value.WallClockSeconds,
            "the HTTP backstop must trip only after monty's own wall clock");
    }

    [Fact]
    public void Monty_request_timeout_at_or_below_the_sandbox_wall_clock_fails_startup_validation()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            // Wall clock 60 s vs the default 30 s HTTP backstop: the transport would
            // kill runs the interpreter was about to return cleanly.
            ["Gert:Tools:Sandbox:WallClockSeconds"] = "60",
        });

        var act = () => provider.GetRequiredService<IOptions<MontyParameters>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*RequestTimeoutSeconds*")
            .WithMessage("*WallClockSeconds*");
    }

    [Fact]
    public void Wall_clock_relation_is_not_enforced_for_the_gvisor_backend()
    {
        using var provider = Provider(new Dictionary<string, string?>
        {
            ["Gert:Tools:Sandbox:Type"] = "gvisor",
            ["Gert:Tools:Sandbox:WallClockSeconds"] = "60",
        });

        // gVisor does not use the monty sidecar, so the relation validator is only
        // registered on the monty path - a long gVisor wall clock must not demand a
        // pointless monty knob change.
        var act = () => provider.GetRequiredService<IOptions<MontyParameters>>().Value;

        act.Should().NotThrow();
    }
}
