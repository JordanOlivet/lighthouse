using FluentAssertions;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

public class ComposeHostPathHelperTests
{
    private const string RootPath = "/app/compose-files";

    private const string RelativeMountsYaml = """
        services:
          qui:
            image: ghcr.io/autobrr/qui:latest
            volumes:
              - ./qui:/config
              - qui-cache:/cache
          vpn:
            image: qmcgaw/gluetun
            volumes:
              - type: bind
                source: ../shared/gluetun
                target: /gluetun
              - /etc/localtime:/etc/localtime:ro
        volumes:
          qui-cache:
        """;

    private const string AbsoluteMountsYaml = """
        services:
          qui:
            image: ghcr.io/autobrr/qui:latest
            volumes:
              - /srv/qui:/config
              - qui-cache:/cache
        """;

    // --- ResolveHostRoot ----------------------------------------------------

    [Fact]
    public void ResolveHostRoot_MountOnRootPath_ReturnsSource()
    {
        string? hostRoot = ComposeHostPathHelper.ResolveHostRoot(
            new (string?, string?)[] { ("/home/v4l/stacks", "/app/compose-files"), ("/var/run/docker.sock", "/var/run/docker.sock") },
            RootPath);

        hostRoot.Should().Be("/home/v4l/stacks");
    }

    [Fact]
    public void ResolveHostRoot_MountOnParentOfRootPath_AppendsRemainder()
    {
        string? hostRoot = ComposeHostPathHelper.ResolveHostRoot(
            new (string?, string?)[] { ("/srv/lighthouse", "/app") },
            RootPath);

        hostRoot.Should().Be("/srv/lighthouse/compose-files");
    }

    [Fact]
    public void ResolveHostRoot_PicksDeepestMount()
    {
        string? hostRoot = ComposeHostPathHelper.ResolveHostRoot(
            new (string?, string?)[] { ("/srv/lighthouse", "/app"), ("/home/v4l/stacks/", "/app/compose-files/") },
            RootPath);

        hostRoot.Should().Be("/home/v4l/stacks");
    }

    [Fact]
    public void ResolveHostRoot_NoCoveringMount_ReturnsNull()
    {
        ComposeHostPathHelper.ResolveHostRoot(
            new (string?, string?)[] { ("/data", "/app/data"), ("/srv/x", "/app/compose-files-old") },
            RootPath).Should().BeNull();
    }

    [Fact]
    public void ResolveHostRoot_WindowsSource_ReturnsNull()
    {
        ComposeHostPathHelper.ResolveHostRoot(
            new (string?, string?)[] { (@"C:\Users\me\stacks", "/app/compose-files") },
            RootPath).Should().BeNull();
    }

    // --- ToHostPath ---------------------------------------------------------

    [Fact]
    public void ToHostPath_PathUnderRoot_IsMapped()
    {
        ComposeHostPathHelper.ToHostPath("/app/compose-files/torrent/docker-compose.yml", RootPath, "/home/v4l/stacks")
            .Should().Be("/home/v4l/stacks/torrent/docker-compose.yml");
    }

    [Fact]
    public void ToHostPath_PathOutsideRoot_IsUnchanged()
    {
        ComposeHostPathHelper.ToHostPath("/app/compose-files-old/docker-compose.yml", RootPath, "/home/v4l/stacks")
            .Should().Be("/app/compose-files-old/docker-compose.yml");
    }

    // --- PathsOverlap -------------------------------------------------------

    [Theory]
    [InlineData("/app/compose-files", "/app/compose-files", true)]
    [InlineData("/app", "/app/compose-files", true)]
    [InlineData("/app/compose-files/sub", "/app/compose-files", true)]
    [InlineData("/home/v4l/stacks", "/app/compose-files", false)]
    [InlineData("/app/compose", "/app/compose-files", false)]
    public void PathsOverlap_DetectsNesting(string a, string b, bool expected)
    {
        ComposeHostPathHelper.PathsOverlap(a, b).Should().Be(expected);
    }

    // --- GetRelativeBindMounts ----------------------------------------------

    [Fact]
    public void GetRelativeBindMounts_ReturnsShortAndLongSyntaxRelativeSources()
    {
        ComposeHostPathHelper.GetRelativeBindMounts(RelativeMountsYaml)
            .Should().Equal("qui: ./qui", "vpn: ../shared/gluetun");
    }

    [Fact]
    public void GetRelativeBindMounts_AbsoluteAndNamedVolumes_ReturnsEmpty()
    {
        ComposeHostPathHelper.GetRelativeBindMounts(AbsoluteMountsYaml).Should().BeEmpty();
    }

    [Fact]
    public void GetRelativeBindMounts_HomeRelativeSource_IsReported()
    {
        const string yaml = """
            services:
              app:
                image: alpine
                volumes:
                  - ~/app:/data
            """;

        ComposeHostPathHelper.GetRelativeBindMounts(yaml).Should().Equal("app: ~/app");
    }

    [Fact]
    public void GetRelativeBindMounts_InvalidYaml_ReturnsEmpty()
    {
        ComposeHostPathHelper.GetRelativeBindMounts("services: [unclosed").Should().BeEmpty();
    }

    // --- GetRelativeMountConflict -------------------------------------------

    [Fact]
    public void GetRelativeMountConflict_StartedElsewhereWithRelativeMounts_ReturnsError()
    {
        string? conflict = ComposeHostPathHelper.GetRelativeMountConflict(
            "torrent", "/app/compose-files/torrent", RelativeMountsYaml,
            new[] { "/home/v4l/stacks/torrent", "/home/v4l/stacks/torrent" });

        conflict.Should().NotBeNull();
        conflict.Should().Contain("/home/v4l/stacks/torrent").And.Contain("qui: ./qui");
    }

    [Fact]
    public void GetRelativeMountConflict_SameWorkingDir_ReturnsNull()
    {
        ComposeHostPathHelper.GetRelativeMountConflict(
            "torrent", "/app/compose-files/torrent/", RelativeMountsYaml,
            new[] { "/app/compose-files/torrent" }).Should().BeNull();
    }

    [Fact]
    public void GetRelativeMountConflict_NoContainers_ReturnsNull()
    {
        ComposeHostPathHelper.GetRelativeMountConflict(
            "torrent", "/app/compose-files/torrent", RelativeMountsYaml,
            Array.Empty<string?>()).Should().BeNull();
    }

    [Fact]
    public void GetRelativeMountConflict_AbsoluteMounts_ReturnsNull()
    {
        ComposeHostPathHelper.GetRelativeMountConflict(
            "torrent", "/app/compose-files/torrent", AbsoluteMountsYaml,
            new[] { "/home/v4l/stacks/torrent" }).Should().BeNull();
    }
}
