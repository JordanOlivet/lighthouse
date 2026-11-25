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
}
