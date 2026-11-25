using FluentAssertions;
using Lighthouse.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lighthouse.Tests.Extensions;

public class OidcAuthenticationExtensionsTests
{
    [Theory]
    [InlineData("https://lh.example.com", "https://lh.example.com/api/auth/oidc/callback")]
    [InlineData("https://lh.example.com/", "https://lh.example.com/api/auth/oidc/callback")]
    [InlineData("http://localhost:5173", "http://localhost:5173/api/auth/oidc/callback")]
    public void BuildCallbackUrl(string baseUrl, string expected)
    {
        OidcAuthenticationExtensions.BuildCallbackUrl(baseUrl).Should().Be(expected);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddOidcAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddOidcAuthentication_Configured_UsesLaxSameAsRequestCookiesAndPkce()
    {
        using var provider = BuildProvider(new()
        {
            ["Oidc:Authority"] = "https://id.example.com",
            ["Oidc:ClientId"] = "lighthouse",
            ["Oidc:ClientSecret"] = "secret"
        });

        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcAuthDefaults.Scheme);

        options.CorrelationCookie.SameSite.Should().Be(SameSiteMode.Lax);
        options.CorrelationCookie.SecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
        options.NonceCookie.SameSite.Should().Be(SameSiteMode.Lax);
        options.NonceCookie.SecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
        options.UsePkce.Should().BeTrue();
        options.ResponseType.Should().Be("code");
        // Default form_post makes the provider return via a cross-site POST, on which
        // browsers drop the SameSite=Lax correlation/nonce cookies ("Correlation failed").
        options.ResponseMode.Should().Be(Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseMode.Query);
        options.CallbackPath.Should().Be(new PathString("/api/auth/oidc/callback"));
        options.SignInScheme.Should().Be(OidcAuthDefaults.ExternalCookieScheme);
        options.MapInboundClaims.Should().BeFalse();
        options.SaveTokens.Should().BeFalse();
        options.Scope.Should().BeEquivalentTo("openid", "email", "profile");
        options.ClientSecret.Should().Be("secret");
        // "iss" is deleted by default; the resolver needs it to key the account link.
        options.ClaimActions.Should().NotContain(a => a.ClaimType == "iss");
    }

    [Fact]
    public async Task AddOidcAuthentication_NotConfigured_RegistersNoOidcScheme()
    {
        using var provider = BuildProvider(new());

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(OidcAuthDefaults.Scheme)).Should().BeNull();
        (await schemes.GetSchemeAsync(OidcAuthDefaults.ExternalCookieScheme)).Should().BeNull();
    }

    [Theory]
    [InlineData(false, "/login?oidc_error=provider_error")]
    [InlineData(true, "/users?oidc_link_error=provider_error")]
    public async Task OnRemoteFailure_RedirectsToLoginOrBackToLinkPage(bool linkMode, string expected)
    {
        using var provider = BuildProvider(new()
        {
            ["Oidc:Authority"] = "https://id.example.com",
            ["Oidc:ClientId"] = "lighthouse"
        });
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcAuthDefaults.Scheme);
        var http = new DefaultHttpContext { RequestServices = provider };
        var properties = new AuthenticationProperties();
        if (linkMode)
        {
            properties.Items[OidcAuthDefaults.LinkUserItem] = "1";
            properties.Items[OidcAuthDefaults.LinkReturnItem] = "/users";
        }
        var context = new RemoteFailureContext(http,
            new AuthenticationScheme(OidcAuthDefaults.Scheme, null, typeof(OpenIdConnectHandler)),
            options, new Exception("access_denied")) { Properties = properties };

        await options.Events.OnRemoteFailure(context);

        http.Response.Headers.Location.ToString().Should().Be(expected);
    }
}
