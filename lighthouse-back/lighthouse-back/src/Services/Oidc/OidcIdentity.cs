using System.Security.Claims;

namespace Lighthouse.Services.Oidc;

/// <summary>
/// The identity asserted by the OIDC provider, extracted from raw (unmapped) claims.
/// </summary>
public record OidcIdentity(string Issuer, string Subject, string? Email, bool EmailVerified, string? PreferredUsername)
{
    /// <summary>
    /// Builds the identity from a principal carrying raw OIDC claim names
    /// (MapInboundClaims = false). Returns null when "iss" or "sub" is missing.
    /// </summary>
    public static OidcIdentity? FromPrincipal(ClaimsPrincipal principal)
    {
        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            return null;

        var email = principal.FindFirst("email")?.Value;
        // email_verified arrives as a JSON boolean, serialized to "true"/"True" depending on the handler.
        var emailVerified = bool.TryParse(principal.FindFirst("email_verified")?.Value, out var verified) && verified;

        return new OidcIdentity(
            issuer,
            subject,
            string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            emailVerified,
            principal.FindFirst("preferred_username")?.Value);
    }
}
