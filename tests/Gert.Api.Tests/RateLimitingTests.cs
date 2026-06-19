using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Gert.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The per-user rate limiter (security F10): over-cap requests get a branded 429,
/// one user's throttle never bleeds into another <c>sub</c>'s partition, and the
/// anonymous liveness probe is outside the limited surface.
/// <para>
/// <see cref="GertApiFactory"/> pins the <c>Testing</c> environment, where
/// <c>Program.cs</c> deliberately skips the limiter so the rest of the suite is
/// never throttled. <c>WithWebHostBuilder</c> callbacks run <b>after</b> the
/// factory's <c>ConfigureWebHost</c>, so re-declaring the environment as
/// <c>Development</c> re-enables the limiter. Development is TestServer-safe here:
/// HSTS/HTTPS-redirect are prod-only, the factory's offline JWT rewiring is
/// environment-independent, and the dev-JWKS branch stays off because
/// <c>Gert:Dev:JwksPath</c> is unset. The cap binds from <c>Gert:RateLimiting</c>
/// (RateLimiting.PolicyOptions) - turned down to 2 permits per 5-minute window so
/// the 429 is reachable in three requests and the fixed window cannot roll
/// mid-test.
/// </para>
/// </summary>
public sealed class RateLimitingTests
{
    /// <summary>Permits per window the tests run with - third request must be rejected.</summary>
    private const int PermitLimit = 2;

    /// <summary>
    /// One derived host with the limiter re-enabled. Tests that assert partition
    /// behaviour MUST create all their clients from the same returned factory -
    /// each <c>WithWebHostBuilder</c> call builds a separate host with its own
    /// independent limiter, which would make cross-partition assertions vacuous.
    /// </summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> LimitedHost(
        GertApiFactory factory) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // re-enable the limiter (skipped under Testing)
            b.UseSetting("Gert:RateLimiting:PermitLimit", PermitLimit.ToString());
            b.UseSetting("Gert:RateLimiting:Window", "00:05:00"); // can't roll mid-test
        });

    private static HttpClient LimitedClient(GertApiFactory factory) =>
        LimitedHost(factory).CreateClient();

    private static void Authenticate(HttpClient client, GertApiFactory factory, string role) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor(role));

    [Fact]
    public async Task Request_over_the_permit_limit_is_rejected_with_a_branded_429_problem()
    {
        using var factory = new GertApiFactory();
        using var client = LimitedClient(factory);
        Authenticate(client, factory, "user");

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.GetAsync("/api/models");
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "requests within the window's cap pass");
        }

        var rejected = await client.GetAsync("/api/models");

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Too Many Requests");
        // GertProblem brand: every problem carries service=gert + a traceId.
        problem.RootElement.GetProperty("service").GetString().Should().Be("gert");
        problem.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_throttled_user_never_throttles_another_sub_partition_isolation()
    {
        using var factory = new GertApiFactory();
        // Both clients MUST share one host: a second WithWebHostBuilder host gets
        // its own limiter, and "the other user is unthrottled" would pass trivially.
        using var limited = LimitedHost(factory);
        using var userClient = limited.CreateClient();
        Authenticate(userClient, factory, "user");

        // Exhaust user A's partition (sub: dev-user) until a request is rejected.
        for (var i = 0; i < PermitLimit; i++)
        {
            (await userClient.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var rejected = await userClient.GetAsync("/api/models");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The actual F10 semantics: the limit is per user, so a different sub
        // (admin -> dev-admin) sails through immediately after A's 429 - on the
        // SAME limiter instance.
        using var adminClient = limited.CreateClient();
        Authenticate(adminClient, factory, "admin");
        var otherUser = await adminClient.GetAsync("/api/models");
        otherUser.StatusCode.Should().Be(
            HttpStatusCode.OK, "the partition key is the token sub - one user's burst must not throttle another");
    }

    [Fact]
    public async Task Same_sub_under_a_different_issuer_is_a_separate_partition()
    {
        const string otherIssuer = "https://other-idp.test.local";

        // Accept a second issuer alongside the factory default, so two IdPs can
        // mint the SAME sub - the collision the iss+sub partition key prevents.
        using var factory = new GertApiFactory().ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                o => o.TokenValidationParameters.ValidIssuers = [otherIssuer]));

        // ONE derived host (each WithWebHostBuilder call spawns a fresh server,
        // and a fresh server means a fresh limiter) - both identities go through it.
        using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // re-enable the limiter (skipped under Testing)
            b.UseSetting("Gert:RateLimiting:PermitLimit", PermitLimit.ToString());
            b.UseSetting("Gert:RateLimiting:Window", "00:05:00"); // can't roll mid-test
        });

        using var clientA = limited.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Tokens.Mint("dev-user", groups: ["gert-users"]));

        for (var i = 0; i < PermitLimit; i++)
        {
            (await clientA.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await clientA.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Same sub, different iss, same host: its own partition, so it sails through.
        using var clientB = limited.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Tokens.Mint("dev-user", iss: otherIssuer, groups: ["gert-users"]));

        var crossIdp = await clientB.GetAsync("/api/models");
        crossIdp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the partition key is iss+sub (the user-folder anchor) - the same sub minted by a different IdP must not share a bucket");
    }

    [Fact]
    public async Task Liveness_probe_stays_unthrottled_while_the_api_surface_is_limited()
    {
        using var factory = new GertApiFactory();
        using var client = LimitedClient(factory);
        Authenticate(client, factory, "user");

        // Prove the limiter is live on the controller surface...
        for (var i = 0; i < PermitLimit; i++)
        {
            (await client.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // ...and that /healthz (anonymous, outside the policy) never is - well past the cap.
        for (var i = 0; i < PermitLimit + 3; i++)
        {
            var health = await client.GetAsync("/healthz");
            health.StatusCode.Should().Be(
                HttpStatusCode.OK, "the liveness probe is not on the rate-limited controller surface");
        }
    }
}
