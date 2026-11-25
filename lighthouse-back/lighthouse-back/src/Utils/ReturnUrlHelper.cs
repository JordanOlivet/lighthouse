namespace Lighthouse.Utils;

/// <summary>
/// Guards post-login redirects against open redirects: only same-origin paths are kept.
/// </summary>
public static class ReturnUrlHelper
{
    public const string DefaultReturnUrl = "/dashboard";

    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
            return DefaultReturnUrl;

        // "//host" and "/\host" are protocol-relative for browsers.
        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
            return DefaultReturnUrl;

        if (returnUrl.Any(char.IsControl))
            return DefaultReturnUrl;

        return returnUrl;
    }
}
