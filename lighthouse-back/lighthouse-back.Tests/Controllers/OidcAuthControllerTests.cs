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

    private async Task WithRefreshCookie(ControllerBase c, int userId)
    {
        var user = await _ctx.Users.SingleAsync(u => u.Id == userId);
        var tokens = await _authService.CreateSessionAsync(user, 1, "ip", "ua");
        c.HttpContext.Request.Headers.Cookie = RefreshCookieExtensions.RefreshCookieName + "=" + tokens.RefreshToken;
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
}
