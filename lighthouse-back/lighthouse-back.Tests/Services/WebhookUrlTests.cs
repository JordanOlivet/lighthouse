using Lighthouse.Services.Notifications;
using FluentAssertions;

namespace Lighthouse.Tests.Services;

public class WebhookUrlTests
{
    [Theory]
    [InlineData("http://apprise:8000/notify/homelab")]
    [InlineData("https://example.com/hook")]
    [InlineData("http://192.168.1.10:8080/x?y=1")]
    public void IsValid_ReturnsTrue(string url)
    {
        WebhookUrl.IsValid(url).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host/x")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    public void IsValid_ReturnsFalse(string? url)
    {
        WebhookUrl.IsValid(url).Should().BeFalse();
    }
}
