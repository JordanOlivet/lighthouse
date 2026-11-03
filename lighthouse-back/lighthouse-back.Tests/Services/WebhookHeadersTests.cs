using Lighthouse.Services.Notifications;
using FluentAssertions;

namespace Lighthouse.Tests.Services;

public class WebhookHeadersTests
{
    private static KeyValuePair<string, string> H(string name, string value) => new(name, value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n  \n")]
    public void TryParse_ReturnsEmpty_ForNullOrBlank(string? text)
    {
        WebhookHeaders.TryParse(text, out IReadOnlyList<KeyValuePair<string, string>> headers, out string? error)
            .Should().BeTrue();
        headers.Should().BeEmpty();
        error.Should().BeNull();
    }

    [Fact]
    public void TryParse_ParsesMultipleLines()
    {
        WebhookHeaders.TryParse("Authorization: Bearer abc\nX-Api-Key: k1", out var headers, out _).Should().BeTrue();
        headers.Should().BeEquivalentTo(new[] { H("Authorization", "Bearer abc"), H("X-Api-Key", "k1") }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void TryParse_KeepsColonsInValue()
    {
        WebhookHeaders.TryParse("X-Callback: http://host:1234/x", out var headers, out _).Should().BeTrue();
        headers.Should().BeEquivalentTo(new[] { H("X-Callback", "http://host:1234/x") }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void TryParse_HandlesCrLf()
    {
        WebhookHeaders.TryParse("A: b\r\nC: d\r\n", out var headers, out _).Should().BeTrue();
        headers.Should().BeEquivalentTo(new[] { H("A", "b"), H("C", "d") }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void TryParse_IgnoresBlankLines()
    {
        WebhookHeaders.TryParse("A: b\n\n   \nC: d", out var headers, out _).Should().BeTrue();
        headers.Should().HaveCount(2);
    }

    [Fact]
    public void TryParse_Fails_WhenColonMissing()
    {
        WebhookHeaders.TryParse("A: b\nnocolon", out _, out string? error).Should().BeFalse();
        error.Should().Contain("2");
    }

    [Theory]
    [InlineData(": v")]
    [InlineData("Bad Name: v")]
    [InlineData("Bad(Name): v")]
    public void TryParse_Fails_ForInvalidName(string text)
    {
        WebhookHeaders.TryParse(text, out _, out string? error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Content-Type: x")]
    [InlineData("content-length: 1")]
    [InlineData("HOST: h")]
    public void TryParse_Fails_ForForbiddenName(string text)
    {
        WebhookHeaders.TryParse(text, out _, out string? error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Mask_ReturnsDots_WhenNonEmpty()
    {
        WebhookHeaders.Mask("A: b").Should().Be("••••••••");
        WebhookHeaders.Mask(null).Should().Be("");
        WebhookHeaders.Mask("").Should().Be("");
    }
}
