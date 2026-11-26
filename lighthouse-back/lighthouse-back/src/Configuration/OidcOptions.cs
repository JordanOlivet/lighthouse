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

    /// <summary>
    /// Require an https:// authority (discovery and token calls). Set to false only for a provider
    /// reached over plain HTTP on a trusted network.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    private bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Configured and usable. A plain-HTTP authority with <see cref="RequireHttpsMetadata"/> on would
    /// make the OIDC handler throw on every request (local login included), so it disables SSO instead.
    /// </summary>
    public bool IsEnabled => IsConfigured && ConfigurationError == null;

    /// <summary>Why a configured OIDC section cannot be used (logged at startup), null when fine.</summary>
    public string? ConfigurationError =>
        IsConfigured && RequireHttpsMetadata && !Authority!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? $"Oidc__Authority '{Authority}' is not https://: SSO login is disabled. " +
              "Use the provider's HTTPS URL, or set Oidc__RequireHttpsMetadata=false if it is only reachable over HTTP on a trusted network."
            : null;
}
