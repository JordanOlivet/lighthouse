using FluentAssertions;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

public class ReturnUrlHelperTests
{
    [Theory]
    [InlineData("/compose?x=1", "/compose?x=1")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData(null, "/dashboard")]
    [InlineData("", "/dashboard")]
    [InlineData("//evil.com", "/dashboard")]
    [InlineData("https://evil.com", "/dashboard")]
    [InlineData("/\\evil.com", "/dashboard")]
    [InlineData("dashboard", "/dashboard")]
    [InlineData("/ok\r\nSet-Cookie: x", "/dashboard")]
    public void Sanitize(string? input, string expected)
    {
        ReturnUrlHelper.Sanitize(input).Should().Be(expected);
    }
}
