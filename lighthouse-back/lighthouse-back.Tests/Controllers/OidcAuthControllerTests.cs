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

    public OidcAuthControllerTests()
    {
        _ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _ctx.Roles.Add(new Role { Id = 1, Name = "user", Permissions = "[]" });
        _ctx.Users.Add(new User { Id = 1, Username = "alice", Email = "alice@x.fr", RoleId = 1, SecurityStamp = "s" });
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

    private OidcAuthController NewController(bool enabled = true)
    {
        var oidc = enabled
            ? new OidcOptions { Authority = Iss, ClientId = "lh", DisplayName = "Pocket ID", AutoRedirect = true }
            : new OidcOptions();

        var monitor = new Mock<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Setup(m => m.Get(OidcAuthDefaults.Scheme))
            .Returns(new OpenIdConnectOptions { ConfigurationManager = _discovery.Object });

        var authService = new AuthService(_ctx, new JwtTokenService(_config), _config,
            Mock.Of<IPasswordHasher>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AuthService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_authn.Object);

        var controller = new OidcAuthController(Options.Create(oidc), _resolver.Object, authService,
            _config, monitor.Object, NullLogger<OidcAuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
        return controller;
    }

    private void ExternalIdentity(params (string, string)[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Item1, c.Item2)), "oidc"));
        _authn.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), OidcAuthDefaults.ExternalCookieScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(principal, OidcAuthDefaults.ExternalCookieScheme)));
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
    public async Task Complete_Success_SetsRefreshCookieAndRedirectsToFrontend()
    {
        ExternalIdentity(("iss", Iss), ("sub", "s1"), ("email", "alice@x.fr"), ("email_verified", "true"));
        var alice = await _ctx.Users.Include(u => u.Role).SingleAsync();
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
        var alice = await _ctx.Users.Include(u => u.Role).SingleAsync();
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<OidcIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OidcResolveResult.Ok(alice));

        var result = await NewController().Complete("//evil.com");

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/oidc-complete?returnUrl=%2Fdashboard");
    }

    [Fact]
    public async Task Login_WhenDisabled_Returns404()
    {
        var result = await NewController(enabled: false).Login("/compose", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Login_ProviderUnreachable_RedirectsProviderError()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IDX20803: Unable to obtain configuration"));

        var result = await NewController().Login("/compose", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/login?oidc_error=provider_error");
    }

    [Fact]
    public async Task Login_ProviderReachable_ChallengesOidcWithCompleteRedirect()
    {
        _discovery.Setup(d => d.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenIdConnectConfiguration());

        var result = await NewController().Login("/compose", CancellationToken.None);

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
}
