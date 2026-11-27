using System.Security.Claims;
using DockerComposeManager.Services.Security;
using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.Controllers;
using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Extensions;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Moq;

namespace Lighthouse.Tests.Controllers;

public class OidcAuthControllerTests
{
    private const string Iss = "https://id.example.com";

    private readonly AppDbContext _ctx;
    private readonly IConfiguration _config;
    private readonly Mock<IAuthenticationService> _authn = new();
    private readonly Mock<IOidcUserResolver> _resolver = new();
    private readonly Mock<IConfigurationManager<OpenIdConnectConfiguration>> _discovery = new();
    private readonly OidcLinkTicketStore _tickets = new(new MemoryCache(new MemoryCacheOptions()));

    public OidcAuthControllerTests()
    {
        _ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _ctx.Roles.Add(new Role { Id = 1, Name = "user", Permissions = "[]" });
        _ctx.Users.Add(new User { Id = 1, Username = "alice", Email = "alice@x.fr", RoleId = 1, SecurityStamp = "s" });
        _ctx.Users.Add(new User { Id = 2, Username = "mallory", Email = "m@x.fr", RoleId = 1, SecurityStamp = "s" });
        _ctx.SaveChanges();

        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-key-at-least-32-characters-long",
            ["Jwt:Issuer"] = "lighthouse",
            ["Jwt:Audience"] = "lighthouse-client",
            ["Jwt:ExpirationMinutes"] = "15",
            ["Jwt:RefreshExpirationDays"] = "1"
        }).Build();
    }

    private AuthService _authService = null!;

    private OidcAuthController NewController(bool enabled = true)
    {
        var oidc = enabled
            ? new OidcOptions { Authority = Iss, ClientId = "lh", DisplayName = "Pocket ID", AutoRedirect = true }
            : new OidcOptions();

        var monitor = new Mock<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Setup(m => m.Get(OidcAuthDefaults.Scheme))
            .Returns(new OpenIdConnectOptions { ConfigurationManager = _discovery.Object });

        _authService = new AuthService(_ctx, new JwtTokenService(_config), _config,
            Mock.Of<IPasswordHasher>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AuthService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_authn.Object);

        var controller = new OidcAuthController(Options.Create(oidc), _resolver.Object, _authService,
            _tickets, _config, monitor.Object, NullLogger<OidcAuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
        return controller;
    }

    private void ExternalIdentity(params (string, string)[] claims) => ExternalIdentity(null, claims);

    private void ExternalIdentity(int? linkUserId, params (string, string)[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Item1, c.Item2)), "oidc"));
        var properties = new AuthenticationProperties();
        if (linkUserId != null)
            properties.Items[OidcAuthDefaults.LinkUserItem] = linkUserId.ToString();
        _authn.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), OidcAuthDefaults.ExternalCookieScheme))
            .ReturnsAsync(AuthenticateResult.Success(
                new AuthenticationTicket(principal, properties, OidcAuthDefaults.ExternalCookieScheme)));
    }

    private static void SignedInAs(ControllerBase c, int userId) =>
        c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Bearer"));

    private async Task<SessionTokens> WithRefreshCookie(ControllerBase c, int userId, int days = 1)
    {
        var user = await _ctx.Users.SingleAsync(u => u.Id == userId);
        var tokens = await _authService.CreateSessionAsync(user, days, "ip", "ua");
        c.HttpContext.Request.Headers.Cookie = RefreshCookieExtensions.RefreshCookieName + "=" + tokens.RefreshToken;
        return tokens;
    }

    private static string SetCookies(ControllerBase c) => c.HttpContext.Response.Headers.SetCookie.ToString();

    [Fact]
    public async Task Complete_ExternalAuthFailed_RedirectsProviderError()
    {
        _authn.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), OidcAuthDefaults.ExternalCookieScheme))
            .ReturnsAsync(AuthenticateResult.NoResult());
        var controller = NewController();

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/login?oidc_error=provider_error");
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
    }

    [Fact]
    public async Task Complete_ResolverRefuses_RedirectsWithCodeAndEmail()
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "bob@x.fr"), ("email_verified", "true"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Fail(OidcErrorCodes.AccountNotFound));
        var controller = NewController();

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url
            .Should().Be("/login?oidc_error=account_not_found&email=bob%40x.fr");
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
        _authn.Verify(a => a.SignOutAsync(It.IsAny<HttpContext>(), OidcAuthDefaults.ExternalCookieScheme,
            It.IsAny<AuthenticationProperties>()), Times.Once);
    }

    [Theory]
    [InlineData("link_required")]
    [InlineData("email_ambiguous")]
    public async Task Complete_LinkRequiredOrAmbiguous_RedirectsWithEmail(string code)
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "bob@x.fr"), ("email_verified", "true"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Fail(code));

        var result = await NewController().Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url
            .Should().Be($"/login?oidc_error={code}&email=bob%40x.fr");
    }

    [Fact]
    public async Task Complete_AdminLinkRequired_RedirectsWithEmail()
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "root@x.fr"), ("email_verified", "true"));
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Fail(OidcErrorCodes.AdminLinkRequired));

        var result = await NewController().Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url
            .Should().Be("/login?oidc_error=admin_link_required&email=root%40x.fr");
    }

    [Fact]
    public async Task Complete_Success_SetsRefreshCookieAndRedirectsToFrontend()
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "alice@x.fr"), ("email_verified", "true"));
        var alice = await _ctx.Users.Include(u => u.Role).SingleAsync(u => u.Id == 1);
        _resolver.Setup(r => r.ResolveAsync(
                It.Is<OidcIdentity>(i => i.Issuer == Iss && i.Subject == "s1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Ok(alice));
        var controller = NewController();

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/oidc-complete?returnUrl=%2Fcompose");
        SetCookies(controller).Should().Contain(RefreshCookieExtensions.RefreshCookieName + "=");
        (await _ctx.Sessions.CountAsync(s => s.UserId == 1)).Should().Be(1);
        _authn.Verify(a => a.SignOutAsync(It.IsAny<HttpContext>(), OidcAuthDefaults.ExternalCookieScheme,
            It.IsAny<AuthenticationProperties>()), Times.Once);
    }

    [Fact]
    public async Task Complete_UnsafeReturnUrl_FallsBackToDashboard()
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "alice@x.fr"), ("email_verified", "true"));
        var alice = await _ctx.Users.Include(u => u.Role).SingleAsync(u => u.Id == 1);
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Ok(alice));

        var result = await NewController().Complete("//evil.com");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/oidc-complete?returnUrl=%2Fdashboard");
    }

    [Fact]
    public async Task Login_WhenDisabled_Returns404()
    {
        var result = await NewController(enabled: false).Login("/compose", null, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Complete_OidcDisabled_ReturnsNotFound()
    {
        // Without SSO the external cookie scheme is not registered: authenticating would throw.
        _authn.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("No authentication handler is registered"));

        var result = await NewController(enabled: false).Complete("/compose");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Login_ProviderUnreachable_RedirectsProviderError()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IDX20803: Unable to obtain configuration"));

        var result = await NewController().Login("/compose", null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/login?oidc_error=provider_error");
    }

    [Fact]
    public async Task Login_ProviderReachable_ChallengesOidcWithCompleteRedirect()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenIdConnectConfiguration());

        var result = await NewController().Login("/compose", null, CancellationToken.None);

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.AuthenticationSchemes.Should().Equal(OidcAuthDefaults.Scheme);
        challenge.Properties!.RedirectUri.Should().Be("/api/auth/oidc/complete?returnUrl=%2Fcompose");
    }

    [Fact]
    public void Config_ReturnsEnabledDisplayNameAutoRedirect()
    {
        var result = NewController().Config();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<OidcConfigDto>>()
            .Which.Data.Should().Be(new OidcConfigDto(true, "Pocket ID", true));
    }

    [Fact]
    public void Config_WhenDisabled_ReportsDisabled()
    {
        var result = NewController(enabled: false).Config();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ((ApiResponse<OidcConfigDto>)ok.Value!).Data!.Enabled.Should().BeFalse();
    }

    // --- Explicit link from an open session -------------------------------------------

    [Fact]
    public void StartLink_ReturnsTicketForCurrentUser()
    {
        var controller = NewController();
        SignedInAs(controller, 1);

        var result = controller.StartLink();

        var ticket = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<OidcLinkStartDto>>().Subject.Data!.Ticket;
        _tickets.Consume(ticket).Should().Be(1);
    }

    [Fact]
    public void StartLink_WhenDisabled_Returns404()
    {
        var controller = NewController(enabled: false);
        SignedInAs(controller, 1);

        controller.StartLink().Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Login_WithValidLinkTicket_ChallengeCarriesLinkedUser()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenIdConnectConfiguration());
        var ticket = _tickets.Issue(1);

        var result = await NewController().Login("/compose", ticket, CancellationToken.None);

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.Properties!.Items[OidcAuthDefaults.LinkUserItem].Should().Be("1");
        challenge.Properties.Items[OidcAuthDefaults.LinkReturnItem].Should().Be("/compose");
        _tickets.Consume(ticket).Should().BeNull("a ticket is single-use");
    }

    [Fact]
    public async Task Login_WithUnknownLinkTicket_RedirectsBackWithLinkInvalid()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenIdConnectConfiguration());

        var result = await NewController().Login("/compose?tab=1", "forged", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/compose?tab=1&oidc_link_error=link_invalid");
    }

    [Fact]
    public async Task Complete_LinkMode_SessionMatches_LinksWithoutNewSession()
    {
        ExternalIdentity(1, ("iss", Iss), ("sub", "s1"), ("email", "other@x.fr"));
        var alice = await _ctx.Users.Include(u => u.Role).SingleAsync(u => u.Id == 1);
        _resolver.Setup(r => r.LinkAsync(1, It.Is<OidcIdentity>(i => i.Subject == "s1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Ok(alice));
        var controller = NewController();
        await WithRefreshCookie(controller, 1);

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/compose?oidc_linked=1");
        (await _ctx.Sessions.CountAsync()).Should().Be(1, "linking keeps the existing session");
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_LinkMode_NoSessionCookie_RedirectsLinkInvalid()
    {
        ExternalIdentity(1, ("iss", Iss), ("sub", "s1"));

        var result = await NewController().Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/compose?oidc_link_error=link_invalid");
        _resolver.Verify(r => r.LinkAsync(It.IsAny<int>(), It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_LinkMode_SessionOfAnotherUser_RedirectsLinkInvalid()
    {
        // Ticket minted by mallory (2), flow finished in alice's browser (session of 1).
        ExternalIdentity(2, ("iss", Iss), ("sub", "s1"));
        var controller = NewController();
        await WithRefreshCookie(controller, 1);

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/compose?oidc_link_error=link_invalid");
        _resolver.Verify(r => r.LinkAsync(It.IsAny<int>(), It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_LinkMode_ResolverRefuses_RedirectsWithCode()
    {
        ExternalIdentity(1, ("iss", Iss), ("sub", "s1"));
        _resolver.Setup(r => r.LinkAsync(1, It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Fail(OidcErrorCodes.IdentityInUse));
        var controller = NewController();
        await WithRefreshCookie(controller, 1);

        var result = await controller.Complete("/compose");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/compose?oidc_link_error=identity_in_use");
    }

    [Theory]
    [InlineData("/users", null, "/users?oidc_linked=1")]
    [InlineData("/users?x=1", "identity_in_use", "/users?x=1&oidc_link_error=identity_in_use")]
    [InlineData("//evil.com", null, "/dashboard?oidc_linked=1")]
    public void LinkResultUrl_AppendsOutcomeToSanitizedReturnUrl(string returnUrl, string? code, string expected)
    {
        OidcAuthDefaults.LinkResultUrl(returnUrl, code).Should().Be(expected);
    }

    // --- Self-service unlink (DELETE /api/auth/oidc/link) ---------------------------------

    private async Task<User> GivenAlice(bool hasPassword, bool linked)
    {
        var alice = await _ctx.Users.SingleAsync(u => u.Id == 1);
        alice.PasswordHash = hasPassword ? "hash" : null;
        alice.OidcIssuer = linked ? Iss : null;
        alice.OidcSubject = linked ? "sub-alice" : null;
        await _ctx.SaveChangesAsync();
        return alice;
    }

    [Fact]
    public async Task Unlink_LinkedAccountWithPassword_RemovesLinkAndReturnsNewAccessToken()
    {
        await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        await WithRefreshCookie(controller, 1);

        var result = await controller.Unlink();

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<OidcUnlinkDto>>().Which.Data!;
        dto.AccessToken.Should().NotBeNullOrEmpty();
        dto.User.OidcLinked.Should().BeFalse();
        dto.User.HasPassword.Should().BeTrue();
        // The endpoint never issues a refresh cookie: the caller keeps the one they presented.
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
        var alice = await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1);
        alice.OidcIssuer.Should().BeNull();
        alice.OidcSubject.Should().BeNull();
    }

    [Fact]
    public async Task Unlink_EndsOtherSessions_AndKeepsTheCallerSessionWithItsExpiry()
    {
        var alice = await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        // The caller signed in with "remember me" (30 days); the other session was opened elsewhere,
        // e.g. through the linked identity.
        var caller = await WithRefreshCookie(controller, 1, days: 30);
        var other = await _authService.CreateSessionAsync(alice, 1, "ip2", "ua2");
        var stampBefore = alice.SecurityStamp;

        await controller.Unlink();

        (await _authService.GetActiveSessionUserIdAsync(other.RefreshToken)).Should().BeNull();
        (await _authService.GetActiveSessionUserIdAsync(caller.RefreshToken)).Should().Be(1);
        var kept = await _ctx.Sessions.AsNoTracking().SingleAsync(s => s.UserId == 1);
        kept.ExpiresAt.Should().Be(caller.RefreshExpiresAt, "the caller's session is kept, not re-issued");
        // Rotated stamp: access tokens issued before the unlink are rejected.
        (await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).SecurityStamp.Should().NotBe(stampBefore);
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
    }

    private async Task AssertUnlinkRefusedForMissingSession(
        ActionResult<ApiResponse<OidcUnlinkDto>> result, ControllerBase controller, string stampBefore)
    {
        var body = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<OidcUnlinkDto>>().Subject;
        body.Success.Should().BeFalse();
        body.ErrorCode.Should().Be(OidcErrorCodes.SessionRequired);
        var alice = await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1);
        alice.OidcSubject.Should().Be("sub-alice");
        alice.SecurityStamp.Should().Be(stampBefore);
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
    }

    [Fact]
    public async Task Unlink_WithoutRefreshCookie_IsRefusedAndKeepsLinkAndSessions()
    {
        // An access token read from local storage and replayed from another machine: valid JWT,
        // no refresh cookie. It must not unlink, end the owner's sessions or obtain a session.
        var alice = await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        var owner = await _authService.CreateSessionAsync(alice, 1, "ip", "ua");

        var result = await controller.Unlink();

        await AssertUnlinkRefusedForMissingSession(result, controller, alice.SecurityStamp);
        (await _authService.GetActiveSessionUserIdAsync(owner.RefreshToken)).Should().Be(1);
        (await _ctx.Sessions.CountAsync(s => s.UserId == 1)).Should().Be(1, "no session is created");
    }

    [Fact]
    public async Task Unlink_WithAnotherUsersRefreshCookie_IsRefused()
    {
        var alice = await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        var owner = await _authService.CreateSessionAsync(alice, 1, "ip", "ua");
        await WithRefreshCookie(controller, 2); // mallory's own live session

        var result = await controller.Unlink();

        await AssertUnlinkRefusedForMissingSession(result, controller, alice.SecurityStamp);
        (await _authService.GetActiveSessionUserIdAsync(owner.RefreshToken)).Should().Be(1);
    }

    [Fact]
    public async Task Unlink_WithAlreadyRotatedRefreshCookie_IsRefusedWithoutRevokingTheSession()
    {
        var alice = await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        var stale = await WithRefreshCookie(controller, 1);
        var rotated = await _authService.RefreshTokenAsync(stale.RefreshToken, "ip");

        var result = await controller.Unlink();

        await AssertUnlinkRefusedForMissingSession(result, controller, alice.SecurityStamp);
        // The front answers a 401 with a refresh and a retry: the rotated session must still be live.
        (await _authService.GetActiveSessionUserIdAsync(rotated.Response!.RefreshToken)).Should().Be(1);
    }

    [Fact]
    public async Task Unlink_NotLinked_ReturnsOkAndLeavesSessionsAlone()
    {
        var alice = await GivenAlice(hasPassword: true, linked: false);
        var controller = NewController();
        SignedInAs(controller, 1);
        var existing = await WithRefreshCookie(controller, 1);
        var stampBefore = alice.SecurityStamp;

        var result = await controller.Unlink();

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<OidcUnlinkDto>>().Which.Data!;
        dto.AccessToken.Should().BeNull();
        dto.User.OidcLinked.Should().BeFalse();
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
        (await _authService.GetActiveSessionUserIdAsync(existing.RefreshToken)).Should().Be(1);
        (await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).SecurityStamp.Should().Be(stampBefore);
    }

    [Fact]
    public async Task Unlink_SsoOnlyAccount_IsRefusedAndKeepsLinkAndSessions()
    {
        await GivenAlice(hasPassword: false, linked: true);
        var controller = NewController();
        SignedInAs(controller, 1);
        var existing = await WithRefreshCookie(controller, 1);

        var result = await controller.Unlink();

        var body = result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ApiResponse<OidcUnlinkDto>>().Subject;
        body.Success.Should().BeFalse();
        body.ErrorCode.Should().Be(OidcErrorCodes.PasswordRequired);
        (await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).OidcSubject.Should().Be("sub-alice");
        (await _authService.GetActiveSessionUserIdAsync(existing.RefreshToken)).Should().Be(1);
        SetCookies(controller).Should().NotContain(RefreshCookieExtensions.RefreshCookieName + "=");
    }

    [Fact]
    public async Task Unlink_DisabledAccount_ReturnsUnauthorized()
    {
        var alice = await GivenAlice(hasPassword: true, linked: true);
        alice.IsEnabled = false;
        await _ctx.SaveChangesAsync();
        var controller = NewController();
        SignedInAs(controller, 1);

        (await controller.Unlink()).Result.Should().BeOfType<UnauthorizedResult>();
        (await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).OidcSubject.Should().Be("sub-alice");
    }

    [Fact]
    public async Task Unlink_WhenDisabled_Returns404()
    {
        await GivenAlice(hasPassword: true, linked: true);
        var controller = NewController(enabled: false);
        SignedInAs(controller, 1);

        (await controller.Unlink()).Result.Should().BeOfType<NotFoundResult>();
        (await _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).OidcSubject.Should().Be("sub-alice");
    }

    [Fact]
    public void Unlink_RequiresAuthentication()
    {
        typeof(OidcAuthController).GetMethod(nameof(OidcAuthController.Unlink))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Config_IsNotRateLimited()
    {
        // Fetched on every page load: a 429 would silently hide the SSO button.
        typeof(OidcAuthController).GetMethod(nameof(OidcAuthController.Config))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.DisableRateLimitingAttribute), false)
            .Should().NotBeEmpty();
    }
}
