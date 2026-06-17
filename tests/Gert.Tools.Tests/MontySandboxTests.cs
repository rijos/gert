using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Service.External;
using Gert.Tools.Sandbox;
using Gert.Tools.Sandbox.Monty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// Drives the real <see cref="MontySandbox"/> through a stubbed handler (asserting the
/// <c>/run</c> request shape + response mapping) and unit-tests the pure
/// <see cref="MontySandbox.MapResponse"/> / <see cref="MontySandbox.MapFailure"/> mapping.
/// No monty sidecar - the live <c>pydantic-monty</c> path is integration-only.
/// </summary>
public sealed class MontySandboxTests
{
    private static MontySandbox NewSandbox(StubHttpMessageHandler handler, PythonSandboxOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://monty.test") };
        return new MontySandbox(http, Options.Create(options ?? new PythonSandboxOptions()), NullLogger<MontySandbox>.Instance);
    }

    private static StubHttpMessageHandler OkJson(string json) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task RunPythonAsync_PostsCodeAndLimits_AndMapsSuccess()
    {
        var handler = OkJson("""{"stdout":"42\n","stderr":"","exit_code":0,"timed_out":false}""");
        var sandbox = NewSandbox(handler);

        var result = await sandbox.RunPythonAsync("print(40 + 2)");

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("42\n");
        result.TimedOut.Should().BeFalse();

        handler.LastRequestUri!.AbsolutePath.Should().Be("/run");
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["code"]!.GetValue<string>().Should().Be("print(40 + 2)");
        body["wall_clock_seconds"]!.GetValue<int>().Should().Be(10);
        body["memory_mib"]!.GetValue<int>().Should().Be(256);
        body["max_output_bytes"]!.GetValue<int>().Should().Be(64 * 1024);
    }

    [Fact]
    public async Task RunPythonAsync_MapsErrorExitAndStderr()
    {
        var handler = OkJson("""{"stdout":"","stderr":"MontyRuntimeError: boom","exit_code":1,"timed_out":false}""");

        var result = await NewSandbox(handler).RunPythonAsync("boom()");

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("boom");
    }

    [Fact]
    public async Task RunPythonAsync_GracefulOnTransportFailure()
    {
        // The sandbox must never throw an infra error into the tool loop.
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));

        var result = await NewSandbox(handler).RunPythonAsync("print(1)");

        result.ExitCode.Should().Be(1);
        result.TimedOut.Should().BeFalse();
        result.Stderr.Should().Contain("Sandbox run failed");
    }

    [Fact]
    public async Task RunPythonAsync_OversizedResponseIsAGracefulFailure()
    {
        // A misbehaving sidecar that streams far more than the per-stream output
        // cap allows must fail the run gracefully (capped read), never balloon
        // host memory or throw an infra error into the tool loop.
        var options = new PythonSandboxOptions { MaxOutputBytes = 16 };
        var huge = new string('a', 64 * 1024);
        var handler = OkJson($$"""{"stdout":"{{huge}}","stderr":"","exit_code":0,"timed_out":false}""");

        var result = await NewSandbox(handler, options).RunPythonAsync("print(1)");

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("Sandbox run failed");
    }

    [Fact]
    public async Task RunPythonAsync_PropagatesCallerCancellation()
    {
        // A cancelled caller token must surface as cancellation, NOT a swallowed result.
        var handler = OkJson("{}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await NewSandbox(handler).Invoking(s => s.RunPythonAsync("print(1)", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void MapResponse_NullPayload_IsNonZeroExit()
    {
        var result = MontySandbox.MapResponse(null);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("empty response");
    }

    [Fact]
    public void MapResponse_MapsAllFields()
    {
        var result = MontySandbox.MapResponse(new MontyRunResponse
        {
            Stdout = "hi",
            Stderr = "warn",
            ExitCode = 3,
            TimedOut = true,
        });

        result.Stdout.Should().Be("hi");
        result.Stderr.Should().Be("warn");
        result.ExitCode.Should().Be(3);
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public void MapFailure_Timeout_IsTimedOut()
    {
        var result = MontySandbox.MapFailure(new TimeoutException());

        result.ExitCode.Should().Be(124);
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public void MapFailure_Cancellation_IsTimedOut()
    {
        // The HTTP-timeout backstop surfaces as a (non-caller) cancellation -> timed out.
        var result = MontySandbox.MapFailure(new OperationCanceledException());

        result.ExitCode.Should().Be(124);
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public void MapFailure_OtherException_IsExitOne()
    {
        var result = MontySandbox.MapFailure(new HttpRequestException("down"));

        result.ExitCode.Should().Be(1);
        result.TimedOut.Should().BeFalse();
        result.Stderr.Should().Contain("down");
    }
}
