using System.Security.Claims;
using FluentAssertions;
using Gert.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gert.Authentication.Tests;

/// <summary>
/// JwtBearer configuration: the RS256 algorithm pin (security F11) and the
/// <c>sub</c>-denylist revocation hook (decisions §4), both verified without
/// booting a server.
/// </summary>
public sealed class JwtAuthConfigTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void ValidAlgorithms_is_pinned_to_rs256()
    {
        var options = new JwtBearerOptions();
        GertJwtAuthExtensions.ConfigureJwtBearer(options, Config());

        options.TokenValidationParameters.ValidAlgorithms
            .Should().Equal("RS256");
    }

    [Fact]
    public void Validation_flags_and_claim_mappings_are_set()
    {
        var options = new JwtBearerOptions();
        GertJwtAuthExtensions.ConfigureJwtBearer(
            options,
            Config(
                ("Auth:Authority", "https://id.test.local"),
                ("Auth:Audience", "gert-api")));

        var tvp = options.TokenValidationParameters;
        tvp.ValidateIssuer.Should().BeTrue();
        tvp.ValidateAudience.Should().BeTrue();
        tvp.ValidateLifetime.Should().BeTrue();
        tvp.ValidateIssuerSigningKey.Should().BeTrue();
        tvp.NameClaimType.Should().Be("preferred_username");
        tvp.RoleClaimType.Should().Be("groups");
        tvp.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));

        options.Authority.Should().Be("https://id.test.local");
        options.Audience.Should().Be("gert-api");
    }

    [Fact]
    public void Denylisted_sub_fails_authentication()
    {
        var denylist = new InMemorySubDenylist();
        denylist.Deny("revoked-user");

        var principal = PrincipalWithSub("revoked-user");
        string? failure = null;

        GertJwtAuthExtensions.ApplyDenylist(principal, denylist, reason => failure = reason);

        failure.Should().NotBeNull();
    }

    [Fact]
    public void Allowed_sub_passes_the_denylist()
    {
        var denylist = new InMemorySubDenylist();
        denylist.Deny("someone-else");

        var principal = PrincipalWithSub("good-user");
        var failed = false;

        GertJwtAuthExtensions.ApplyDenylist(principal, denylist, _ => failed = true);

        failed.Should().BeFalse();
    }

    [Fact]
    public void InMemory_denylist_deny_then_allow_round_trips()
    {
        var denylist = new InMemorySubDenylist();

        denylist.IsDenied("x").Should().BeFalse();
        denylist.Deny("x");
        denylist.IsDenied("x").Should().BeTrue();
        denylist.Allow("x");
        denylist.IsDenied("x").Should().BeFalse();
    }

    private static ClaimsPrincipal PrincipalWithSub(string sub) =>
        new(new ClaimsIdentity([new Claim("sub", sub)], authenticationType: "Test"));
}
