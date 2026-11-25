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

public record OidcLinkStartDto(string Ticket);

/// <summary>
/// SSO login via OpenID Connect. Flow:
/// GET login → provider → /api/auth/oidc/callback (handler) → GET complete → frontend /auth/oidc-complete.
/// Explicit link (signed-in user): POST link → ticket → GET login?linkTicket= → provider → GET complete,
/// which links the account of the open session and returns to the starting page.
/// </summary>
[ApiController]
[Route("api/auth/oidc")]
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
        OidcErrorCodes.SubjectMismatch,
        OidcErrorCodes.AdminLinkRequired
    };

    private readonly OidcOptions _options;
    private readonly IOidcUserResolver _resolver;
    private readonly AuthService _authService;
    private readonly OidcLinkTicketStore _linkTickets;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcHandlerOptions;
    private readonly ILogger<OidcAuthController> _logger;

    public OidcAuthController(
        IOptions<OidcOptions> options,
        IOidcUserResolver resolver,
        AuthService authService,
        OidcLinkTicketStore linkTickets,
        IConfiguration configuration,
        IOptionsMonitor<OpenIdConnectOptions> oidcHandlerOptions,
        ILogger<OidcAuthController> logger)
    {
        _options = options.Value;
        _resolver = resolver;
        _authService = authService;
        _linkTickets = linkTickets;
        _configuration = configuration;
        _oidcHandlerOptions = oidcHandlerOptions;
        _logger = logger;
    }

    [HttpGet("config")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<OidcConfigDto>> Config() =>
        Ok(ApiResponse.Ok(new OidcConfigDto(_options.IsEnabled, _options.DisplayName, _options.IsEnabled && _options.AutoRedirect)));

    /// <summary>
    /// Starts an explicit link for the signed-in user. The access token never leaves local
    /// storage, so the browser navigation to the provider carries this single-use ticket instead.
    /// </summary>
    [HttpPost("link")]
    [Authorize]
    public ActionResult<ApiResponse<OidcLinkStartDto>> StartLink()
    {
        if (!_options.IsEnabled)
            return NotFound();

        return Ok(ApiResponse.Ok(new OidcLinkStartDto(_linkTickets.Issue(GetCurrentUserIdRequired()))));
    }

    [HttpGet("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl, [FromQuery] string? linkTicket, CancellationToken ct)
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
            return Redirect(linkTicket != null
                ? OidcAuthDefaults.LinkResultUrl(returnUrl, OidcErrorCodes.ProviderError)
                : ErrorUrl(OidcErrorCodes.ProviderError, null));
        }

        var safeReturnUrl = ReturnUrlHelper.Sanitize(returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/api/auth/oidc/complete?returnUrl=" + Uri.EscapeDataString(safeReturnUrl)
        };

        if (linkTicket != null)
        {
            var linkUserId = _linkTickets.Consume(linkTicket);
            if (linkUserId == null)
                return Redirect(OidcAuthDefaults.LinkResultUrl(safeReturnUrl, OidcErrorCodes.LinkInvalid));

            // Carried in the protected state, then in the external cookie, to Complete.
            properties.Items[OidcAuthDefaults.LinkUserItem] = linkUserId.Value.ToString();
            properties.Items[OidcAuthDefaults.LinkReturnItem] = safeReturnUrl;
        }

        return Challenge(properties, OidcAuthDefaults.Scheme);
    }

    [HttpGet("complete")]
    [AllowAnonymous]
    public async Task<IActionResult> Complete([FromQuery] string? returnUrl)
    {
        var external = await HttpContext.AuthenticateAsync(OidcAuthDefaults.ExternalCookieScheme);
        var identity = external.Succeeded ? OidcIdentity.FromPrincipal(external.Principal!) : null;
        if (identity == null)
        {
            await HttpContext.SignOutAsync(OidcAuthDefaults.ExternalCookieScheme);
            return Redirect(ErrorUrl(OidcErrorCodes.ProviderError, null));
        }

        if (external.Properties?.Items.TryGetValue(OidcAuthDefaults.LinkUserItem, out var linkUser) == true)
        {
            await HttpContext.SignOutAsync(OidcAuthDefaults.ExternalCookieScheme);
            return Redirect(await CompleteLinkAsync(int.Parse(linkUser!), identity, returnUrl));
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

    /// <summary>
    /// The browser finishing the flow must still hold the session that asked for the link:
    /// a ticket minted by someone else cannot attach the victim's provider identity to the
    /// attacker's account. The existing session is kept as is (no new one).
    /// </summary>
    private async Task<string> CompleteLinkAsync(int linkUserId, OidcIdentity identity, string? returnUrl)
    {
        var sessionUserId = await _authService.GetActiveSessionUserIdAsync(
            Request.Cookies[RefreshCookieExtensions.RefreshCookieName]);
        if (sessionUserId != linkUserId)
        {
            _logger.LogWarning("OIDC link for user {UserId} refused: browser session belongs to {SessionUserId}",
                linkUserId, sessionUserId);
            return OidcAuthDefaults.LinkResultUrl(returnUrl, OidcErrorCodes.LinkInvalid);
        }

        var result = await _resolver.LinkAsync(linkUserId, identity, HttpContext.RequestAborted);
        return OidcAuthDefaults.LinkResultUrl(returnUrl, result.Success ? null : result.ErrorCode);
    }

    private static string ErrorUrl(string code, string? email)
    {
        var url = "/login?oidc_error=" + code;
        if (email != null && CodesWithEmail.Contains(code))
            url += "&email=" + Uri.EscapeDataString(email);
        return url;
    }
}
