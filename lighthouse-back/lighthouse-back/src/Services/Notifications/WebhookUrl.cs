namespace Lighthouse.Services.Notifications;

/// <summary>
/// Validation for generic webhook URLs: any absolute http(s) URL. Plain http is
/// allowed because receivers (e.g. Apprise) usually live on the LAN / docker network.
/// </summary>
public static class WebhookUrl
{
    /// <summary>
    /// Returns true if the value is an absolute http or https URL.
    /// Empty/whitespace is considered invalid (callers allow empty separately to clear the setting).
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
