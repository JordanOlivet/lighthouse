using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Extensions;
using Lighthouse.Middleware;
using Lighthouse.Services;
using Lighthouse.Services.Oidc;
using Lighthouse.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lighthouse.Controllers;

public record OidcConfigDto(bool Enabled, string DisplayName, bool AutoRedirect);

/// <summary>
/// SSO login via OpenID Connect. Flow:
/// GET login → provider → /api/auth/oidc/callback (handler) → GET complete → frontend /auth/oidc-complete.
/// </summary>
[ApiController]
[Route("api/auth/oidc")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingConfiguration.OidcPolicy)]
public class OidcAuthController : BaseController
{
    private const string FrontendCompletePath = "/auth/oidc-complete";

    // Codes for which the login page shows the email the provider sent.
    private static readonly HashSet<string> CodesWithEmail = new()
    {
        OidcErrorCodes.AccountNotFound,
        OidcErrorCodes.AccountDisabled,
        OidcErrorCodes.EmailNotVerified,
        OidcErrorCodes.SubjectMismatch
    };

    private readonly OidcOptions _options;
    private readonly IOidcUserResolver _resolver;
    private readonly AuthService _authService;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcHandlerOptions;
    private readonly ILogger<OidcAuthController> _logger;

    public OidcAuthController(
        IOptions<OidcOptions> options,
        IOidcUserResolver resolver,
        AuthService authService,
        IConfiguration configuration,
        IOptionsMonitor<OpenIdConnectOptions> oidcHandlerOptions,
        ILogger<OidcAuthController> logger)
    {
        _options = options.Value;
        _resolver = resolver;
        _authService = authService;
        _configuration = configuration;
        _oidcHandlerOptions = oidcHandlerOptions;
        _logger = logger;
    }

    [HttpGet("config")]
    public ActionResult<ApiResponse<OidcConfigDto>> Config() =>
        Ok(ApiResponse.Ok(new OidcConfigDto(_options.IsEnabled, _options.DisplayName, _options.IsEnabled && _options.AutoRedirect)));

    [HttpGet("login")]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl, CancellationToken ct)
    {
        if (!_options.IsEnabled)
            return NotFound();

        // Discovery happens during the challenge, outside OnRemoteFailure: an unreachable
        // provider would surface as a raw 500. Fetch (and cache) it first.
        try
        {
            await _oidcHandlerOptions.Get(OidcAuthDefaults.Scheme).ConfigurationManager!.GetConfigurationAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "OIDC provider {Authority} unreachable", _options.Authority);
            return Redirect(ErrorUrl(OidcErrorCodes.ProviderError, null));
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = "/api/auth/oidc/complete?returnUrl=" + Uri.EscapeDataString(ReturnUrlHelper.Sanitize(returnUrl))
        };
        return Challenge(properties, OidcAuthDefaults.Scheme);
    }

    [HttpGet("complete")]
    public async Task<IActionResult> Complete([FromQuery] string? returnUrl)
    {
        var external = await HttpContext.AuthenticateAsync(OidcAuthDefaults.ExternalCookieScheme);
        var identity = external.Succeeded ? OidcIdentity.FromPrincipal(external.Principal!) : null;
        if (identity == null)
        {
            await HttpContext.SignOutAsync(OidcAuthDefaults.ExternalCookieScheme);
            return Redirect(ErrorUrl(OidcErrorCodes.ProviderError, null));
        }

        var result = await _resolver.ResolveAsync(identity, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(OidcAuthDefaults.ExternalCookieScheme);

        if (!result.Success)
            return Redirect(ErrorUrl(result.ErrorCode!, identity.Email));

        var user = result.User!;
        var refreshDays = int.Parse(_configuration["Jwt:RefreshExpirationDays"] ?? "1");
        var tokens = await _authService.CreateSessionAsync(user, refreshDays, GetUserIpAddress(), GetUserAgent());
        Response.SetRefreshCookie(tokens.RefreshToken, tokens.RefreshExpiresAt, Request.IsHttps);

        _logger.LogInformation("User {Username} (id {UserId}) logged in via OIDC", user.Username, user.Id);
        return Redirect(FrontendCompletePath + "?returnUrl=" + Uri.EscapeDataString(ReturnUrlHelper.Sanitize(returnUrl)));
    }

    private static string ErrorUrl(string code, string? email)
    {
        var url = "/login?oidc_error=" + code;
        if (email != null && CodesWithEmail.Contains(code))
            url += "&email=" + Uri.EscapeDataString(email);
        return url;
    }
}
