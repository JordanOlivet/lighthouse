using FluentAssertions;
using Lighthouse.Configuration;

namespace Lighthouse.Tests.Configuration;

public class OidcOptionsTests
{
    [Fact]
    public void HttpsAuthority_IsEnabled()
    {
        var options = new OidcOptions { Authority = "https://id.example.com", ClientId = "lh" };

        options.IsEnabled.Should().BeTrue();
        options.ConfigurationError.Should().BeNull();
    }

    [Fact]
    public void HttpAuthority_IsDisabledWithAnExplanation()
    {
        var options = new OidcOptions { Authority = "http://id.example.lan", ClientId = "lh" };

        options.IsEnabled.Should().BeFalse();
        options.ConfigurationError.Should().Contain("Oidc__RequireHttpsMetadata");
    }

    [Fact]
    public void HttpAuthority_AllowedWhenHttpsMetadataNotRequired()
    {
        var options = new OidcOptions { Authority = "http://id.example.lan", ClientId = "lh", RequireHttpsMetadata = false };

        options.IsEnabled.Should().BeTrue();
        options.ConfigurationError.Should().BeNull();
    }

    [Fact]
    public void NotConfigured_IsDisabledWithoutError()
    {
        new OidcOptions().ConfigurationError.Should().BeNull();
    }
}
