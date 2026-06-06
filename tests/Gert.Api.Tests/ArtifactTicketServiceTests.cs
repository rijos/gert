using FluentAssertions;
using Gert.Api.Security;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The HMAC capability ticket that authorizes a cross-origin artifact render
/// (security F3). These prove the crypto core: a valid ticket round-trips its
/// bound <c>(iss, sub, pid, artifactId)</c>, and every tampering / expiry /
/// wrong-key path is rejected — so a leaked or forged URL can't read an artifact.
/// </summary>
public sealed class ArtifactTicketServiceTests
{
    private const string Iss = "https://id.dev.local";
    private const string Sub = "dev-user";

    // A clock we can advance to test expiry without sleeping.
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static ArtifactTicketService NewService(
        out MutableClock clock,
        string secret = "test-secret-key-please-ignore",
        TimeSpan? lifetime = null)
    {
        clock = new MutableClock(DateTimeOffset.UnixEpoch.AddYears(56));
        var options = new ArtifactTicketOptions
        {
            Secret = secret,
            Lifetime = lifetime ?? TimeSpan.FromMinutes(5),
        };
        return new ArtifactTicketService(options, clock);
    }

    [Fact]
    public void Valid_ticket_round_trips_its_bound_identifiers()
    {
        var svc = NewService(out _);
        var ticket = svc.Mint(Iss, Sub, "default", "art-123");

        svc.TryValidate(ticket, out var payload).Should().BeTrue();
        payload.Iss.Should().Be(Iss);
        payload.Sub.Should().Be(Sub);
        payload.Pid.Should().Be("default");
        payload.ArtifactId.Should().Be("art-123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    [InlineData("missing.signature")]
    [InlineData(".onlysig")]
    public void Malformed_tickets_are_rejected(string? ticket)
    {
        var svc = NewService(out _);
        svc.TryValidate(ticket, out _).Should().BeFalse();
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var svc = NewService(out _);
        var ticket = svc.Mint(Iss, Sub, "default", "art-123");

        // Flip a char in the base64url payload (before the '.') — the signature no
        // longer matches the altered claims.
        var dot = ticket.IndexOf('.');
        var firstChar = ticket[0] == 'A' ? 'B' : 'A';
        var tampered = firstChar + ticket[1..dot] + ticket[dot..];

        svc.TryValidate(tampered, out _).Should().BeFalse();
    }

    [Fact]
    public void Forged_signature_from_a_different_key_is_rejected()
    {
        var minted = NewService(out _, secret: "key-A").Mint(Iss, Sub, "default", "art-123");
        var attacker = NewService(out _, secret: "key-B");

        attacker.TryValidate(minted, out _).Should().BeFalse();
    }

    [Fact]
    public void Expired_ticket_is_rejected()
    {
        var svc = NewService(out var clock, lifetime: TimeSpan.FromMinutes(5));
        var ticket = svc.Mint(Iss, Sub, "default", "art-123");

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        svc.TryValidate(ticket, out _).Should().BeFalse();
    }

    [Fact]
    public void Ticket_just_inside_its_window_still_validates()
    {
        var svc = NewService(out var clock, lifetime: TimeSpan.FromMinutes(5));
        var ticket = svc.Mint(Iss, Sub, "default", "art-123");

        clock.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));

        svc.TryValidate(ticket, out var payload).Should().BeTrue();
        payload.Pid.Should().Be("default");
    }
}
