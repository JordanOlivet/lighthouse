using FluentAssertions;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

public class ComposeVariableInterpolatorTests
{
    private static string Interpolate(string input, IDictionary<string, string>? vars, out IReadOnlyList<string> unresolved)
    {
        vars ??= new Dictionary<string, string>();
        return ComposeVariableInterpolator.Interpolate(input, n => vars.TryGetValue(n, out string? v) ? v : null, out unresolved);
    }

    private static string Interpolate(string input, IDictionary<string, string>? vars = null)
        => Interpolate(input, vars, out _);

    [Fact]
    public void NoVariables_ReturnedUnchanged()
        => Interpolate("ghcr.io/owner/app:1.2").Should().Be("ghcr.io/owner/app:1.2");

    [Fact]
    public void CoderStyleImage_UsesDefaultsWhenUnset()
    {
        // Real case from production: the update check failed on this unresolved reference.
        string image = Interpolate("${CODER_REPO:-ghcr.io/coder/coder}:${CODER_VERSION:-latest}", null, out var unresolved);

        image.Should().Be("ghcr.io/coder/coder:latest");
        unresolved.Should().BeEmpty();
    }

    [Fact]
    public void CoderStyleImage_UsesValuesWhenSet()
        => Interpolate("${CODER_REPO:-ghcr.io/coder/coder}:${CODER_VERSION:-latest}",
                new Dictionary<string, string> { ["CODER_VERSION"] = "v2.20.0" })
            .Should().Be("ghcr.io/coder/coder:v2.20.0");

    [Theory]
    [InlineData("${TAG}", "1.0")]
    [InlineData("$TAG", "1.0")]
    [InlineData("app:$TAG-alpine", "app:1.0-alpine")]
    [InlineData("${TAG:-x}", "1.0")]
    [InlineData("${TAG-x}", "1.0")]
    [InlineData("${TAG:+set}", "set")]
    [InlineData("${TAG+set}", "set")]
    [InlineData("${TAG:?required}", "1.0")]
    public void SetVariable(string input, string expected)
        => Interpolate(input, new Dictionary<string, string> { ["TAG"] = "1.0" }).Should().Be(expected);

    [Theory]
    [InlineData("${TAG:-x}", "x")]
    [InlineData("${TAG-x}", "")]   // set-but-empty keeps the empty value
    [InlineData("${TAG:+set}", "")]
    [InlineData("${TAG+set}", "set")]
    public void EmptyVariable(string input, string expected)
        => Interpolate(input, new Dictionary<string, string> { ["TAG"] = "" }).Should().Be(expected);

    [Theory]
    [InlineData("${TAG-x}", "x")]
    [InlineData("${TAG:+set}", "")]
    [InlineData("${TAG+set}", "")]
    public void UnsetVariable_WithModifier(string input, string expected)
        => Interpolate(input).Should().Be(expected);

    [Fact]
    public void UnsetVariableWithoutDefault_ReportedAsUnresolvedAndEmpty()
    {
        string result = Interpolate("nginx:${TAG}", null, out var unresolved);

        result.Should().Be("nginx:");
        unresolved.Should().Equal("TAG");
    }

    [Fact]
    public void NestedDefault_IsInterpolated()
        => Interpolate("${IMAGE:-${REGISTRY:-docker.io}/library/nginx}",
                new Dictionary<string, string> { ["REGISTRY"] = "mirror.local" })
            .Should().Be("mirror.local/library/nginx");

    [Fact]
    public void DoubleDollar_IsLiteralDollar()
        => Interpolate("a$$b").Should().Be("a$b");

    [Fact]
    public void UnterminatedBrace_KeptVerbatim()
        => Interpolate("app:${TAG").Should().Be("app:${TAG");

    [Fact]
    public void ParseDotEnv_HandlesCommentsQuotesExportAndInlineComments()
    {
        const string content = """
            # comment
            CODER_VERSION=v2.1.0
            export REPO = ghcr.io/me/app
            QUOTED="with # hash"
            SINGLE='single'
            INLINE=value # trailing comment
            EMPTY=
            not a pair
            """;

        Dictionary<string, string> vars = ComposeVariableInterpolator.ParseDotEnv(content.Replace("\n", "\r\n"));

        vars.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["CODER_VERSION"] = "v2.1.0",
            ["REPO"] = "ghcr.io/me/app",
            ["QUOTED"] = "with # hash",
            ["SINGLE"] = "single",
            ["INLINE"] = "value",
            ["EMPTY"] = ""
        });
    }
}
