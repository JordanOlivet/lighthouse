namespace Lighthouse.Services.Oidc;

/// <summary>
/// Reasons an OIDC login is refused. Sent to the frontend as /login?oidc_error=&lt;code&gt;.
/// </summary>
public static class OidcErrorCodes
{
    public const string AccountNotFound = "account_not_found";
    public const string AccountDisabled = "account_disabled";
    public const string EmailMissing = "email_missing";
    public const string EmailNotVerified = "email_not_verified";
    public const string SubjectMismatch = "subject_mismatch";
    public const string ProviderError = "provider_error";

    /// <summary>Admin account not linked yet: admins are never linked by email, only from an open session.</summary>
    public const string AdminLinkRequired = "admin_link_required";

    /// <summary>Explicit link refused: this provider identity already belongs to another account.</summary>
    public const string IdentityInUse = "identity_in_use";

    /// <summary>Explicit link refused: the link request expired or does not match the open session.</summary>
    public const string LinkInvalid = "link_invalid";
}
