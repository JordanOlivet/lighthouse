using System.Security.Claims;
using FluentAssertions;
using Lighthouse.Services.Oidc;

namespace Lighthouse.Tests.Services;

public class OidcIdentityTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void FromPrincipal_ParsesEmailVerified(string? raw, bool expected)
    {
        var claims = new List<(string, string)> { ("iss", "https://id.example.com"), ("sub", "abc"), ("email", "a@x.fr") };
        if (raw != null) claims.Add(("email_verified", raw));

        var identity = OidcIdentity.FromPrincipal(Principal(claims.ToArray()));

        identity.Should().NotBeNull();
        identity!.EmailVerified.Should().Be(expected);
    }

    [Fact]
    public void FromPrincipal_MapsAllClaims()
    {
        var identity = OidcIdentity.FromPrincipal(Principal(
            ("iss", "https://id.example.com"), ("sub", "abc"), ("email", "a@x.fr"),
            ("email_verified", "true"), ("preferred_username", "alice")));

        identity.Should().Be(new OidcIdentity("https://id.example.com", "abc", "a@x.fr", true, "alice"));
    }

    [Fact]
    public void FromPrincipal_MissingSub_ReturnsNull()
    {
        OidcIdentity.FromPrincipal(Principal(("iss", "https://id.example.com"), ("email", "a@x.fr")))
            .Should().BeNull();
    }

    [Fact]
    public void FromPrincipal_MissingIss_ReturnsNull()
    {
        OidcIdentity.FromPrincipal(Principal(("sub", "abc"))).Should().BeNull();
    }
}
