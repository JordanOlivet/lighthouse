using Lighthouse.Configuration;
using Lighthouse.Services.Oidc;
using Lighthouse.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Lighthouse.Extensions;

public static class OidcAuthDefaults
{
    public const string Scheme = "oidc";

    /// <summary>Short-lived cookie holding the provider identity between callback and /complete.</summary>
    public const string ExternalCookieScheme = "lh_oidc_ext";

    public const string CallbackPath = "/api/auth/oidc/callback";
    public const string CookiePath = "/api/auth/oidc";

    /// <summary>Challenge property: id of the signed-in user who asked to link (explicit link mode).</summary>
    public const string LinkUserItem = "lh_link_uid";

    /// <summary>Challenge property: page to return to after an explicit link.</summary>
    public const string LinkReturnItem = "lh_link_return";

    /// <summary>
    /// Where an explicit link ends: back on the page the user started from, with
    /// <c>oidc_linked=1</c> or <c>oidc_link_error=&lt;code&gt;</c> for the frontend to report.
    /// </summary>
    public static string LinkResultUrl(string? returnUrl, string? errorCode)
    {
        var url = ReturnUrlHelper.Sanitize(returnUrl);
        var separator = url.Contains('?') ? '&' : '?';
        return errorCode == null
            ? $"{url}{separator}oidc_linked=1"
            : $"{url}{separator}oidc_link_error={Uri.EscapeDataString(errorCode)}";
    }
}

/// <summary>
/// Registers the OpenID Connect handler (authorization code + PKCE) used for SSO login.
/// The handler only establishes the provider identity in a temporary cookie; the
/// Lighthouse session itself is opened by OidcAuthController.Complete.
/// </summary>
public static class OidcAuthenticationExtensions
{
    private const string DefaultAppBaseUrl = "http://localhost:3030";

    public static string BuildCallbackUrl(string appBaseUrl) =>
        appBaseUrl.TrimEnd('/') + OidcAuthDefaults.CallbackPath;

    public static AuthenticationBuilder AddOidcAuthentication(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        if (!oidc.IsEnabled)
            return builder;

        var callbackUrl = BuildCallbackUrl(configuration["Email:AppBaseUrl"] ?? DefaultAppBaseUrl);

        builder.AddCookie(OidcAuthDefaults.ExternalCookieScheme, options =>
        {
            options.Cookie.Name = OidcAuthDefaults.ExternalCookieScheme;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = OidcAuthDefaults.CookiePath;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
            // Never redirect API callers to a login page.
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        });

        builder.AddOpenIdConnect(OidcAuthDefaults.Scheme, options =>
        {
            options.Authority = oidc.Authority;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            options.ClientId = oidc.ClientId;
            options.ClientSecret = oidc.ClientSecret;
            options.ResponseType = "code";
            // Return as a top-level GET (default is form_post): browsers drop the Lax
            // correlation/nonce cookies on a cross-site POST. Safe with code + PKCE + state + nonce.
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.MapInboundClaims = false;
            options.CallbackPath = OidcAuthDefaults.CallbackPath;
            options.SignInScheme = OidcAuthDefaults.ExternalCookieScheme;

            options.Scope.Clear();
            foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                options.Scope.Add(scope);

            // The issuer keys the account link; the default claim actions delete it.
            options.ClaimActions.Remove("iss");
            options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
            options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");

            // ASP.NET defaults to SameSite=None; Secure, which browsers drop when the app is
            // served over plain HTTP (http://host:3030). The provider's redirect back is a
            // top-level GET, so Lax is enough.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            options.Events = new OpenIdConnectEvents
            {
                // Behind Nginx the scheme/host Kestrel sees are not the public ones.
                OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.RedirectUri = callbackUrl;
                    return Task.CompletedTask;
                },
                OnRemoteFailure = ctx =>
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(OidcAuthenticationExtensions))
                        .LogWarning("OIDC remote failure: {Error}", ctx.Failure?.Message);
                    // An explicit link started from an open session goes back to that page, not to /login.
                    var target = ctx.Properties?.Items.ContainsKey(OidcAuthDefaults.LinkUserItem) == true
                        ? OidcAuthDefaults.LinkResultUrl(
                            ctx.Properties.Items.TryGetValue(OidcAuthDefaults.LinkReturnItem, out var back) ? back : null, OidcErrorCodes.ProviderError)
                        : "/login?oidc_error=" + OidcErrorCodes.ProviderError;
                    ctx.Response.Redirect(target);
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }
            };
        });

        return builder;
    }
}
