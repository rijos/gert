using FluentAssertions;
using Gert.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gert.Authentication.Tests;

/// <summary>
/// JwtBearer configuration: the RS256 algorithm pin (security F11), verified without
/// booting a server. Revocation is stateless (token expiry + IdP deactivation), so
/// there is no denylist to test (decisions §4).
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
}
