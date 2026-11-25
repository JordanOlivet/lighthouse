namespace Lighthouse.Configuration;

/// <summary>
/// OpenID Connect (SSO) login settings, bound from the "Oidc" section
/// (environment: Oidc__Authority, Oidc__ClientId, ...).
/// OIDC is enabled only when both <see cref="Authority"/> and <see cref="ClientId"/> are set.
/// </summary>
public class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>Issuer URL of the provider (e.g. https://id.example.com).</summary>
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Provider name shown on the login button ("Sign in with {DisplayName}").</summary>
    public string DisplayName { get; set; } = "SSO";

    /// <summary>Redirect /login straight to the provider.</summary>
    public bool AutoRedirect { get; set; }

    /// <summary>Create a Lighthouse account (role "user") for unknown identities.</summary>
    public bool AutoProvision { get; set; }

    /// <summary>Require email_verified == true before linking or creating an account by email.</summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>Space-separated scopes requested from the provider.</summary>
    public string Scopes { get; set; } = "openid email profile";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
