using FluentAssertions;
using Lighthouse.Utils;

namespace Lighthouse.Tests.Utils;

public class ComposeNamespaceDependencyHelperTests
{
    private const string VpnStackYaml = """
        version: "2.1"
        services:
          qbittorrent:
            network_mode: "service:vpn"
            image: lscr.io/linuxserver/qbittorrent:latest
            container_name: qbittorrent
          vpn:
            container_name: vpn
            image: qmcgaw/gluetun
          web:
            image: nginx:latest
        """;

    [Fact]
    public void GetDependentServices_NetworkModeService_ReturnsDependent()
    {
        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            VpnStackYaml, new[] { "vpn" });

        dependents.Should().Equal("qbittorrent");
    }

    [Fact]
    public void GetDependentServices_UpdatedServiceIsNotAParent_ReturnsEmpty()
    {
        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            VpnStackYaml, new[] { "web" });

        dependents.Should().BeEmpty();
    }

    [Fact]
    public void GetDependentServices_UpdatedDependentItself_IsNotReturned()
    {
        // Updating qbittorrent does not require recreating vpn (dependency is one-way).
        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            VpnStackYaml, new[] { "qbittorrent" });

        dependents.Should().BeEmpty();
    }

    [Fact]
    public void GetDependentServices_TransitiveDependents_AreIncluded()
    {
        const string yaml = """
            services:
              a:
                image: a:latest
                network_mode: "service:b"
              b:
                image: b:latest
                network_mode: "service:c"
              c:
                image: c:latest
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "c" });

        dependents.Should().Equal("a", "b");
    }

    [Fact]
    public void GetDependentServices_ContainerReference_MatchesContainerName()
    {
        const string yaml = """
            services:
              app:
                image: app:latest
                network_mode: "container:my-vpn"
              vpn:
                image: vpn:latest
                container_name: my-vpn
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "vpn" });

        dependents.Should().Equal("app");
    }

    [Theory]
    [InlineData("ipc")]
    [InlineData("pid")]
    public void GetDependentServices_IpcAndPidNamespaces_AreDetected(string key)
    {
        string yaml = $"""
            services:
              app:
                image: app:latest
                {key}: "service:parent"
              parent:
                image: parent:latest
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "parent" });

        dependents.Should().Equal("app");
    }

    [Theory]
    [InlineData("host")]
    [InlineData("bridge")]
    [InlineData("none")]
    public void GetDependentServices_NonServiceNetworkModes_AreIgnored(string mode)
    {
        string yaml = $"""
            services:
              app:
                image: app:latest
                network_mode: "{mode}"
              other:
                image: other:latest
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "other" });

        dependents.Should().BeEmpty();
    }

    [Fact]
    public void GetDependentServices_ReferenceToUnknownService_IsIgnored()
    {
        const string yaml = """
            services:
              app:
                image: app:latest
                network_mode: "service:not-in-this-file"
              other:
                image: other:latest
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "other" });

        dependents.Should().BeEmpty();
    }

    [Fact]
    public void GetDependentServices_MultipleDependentsOnSameParent_AllReturned()
    {
        const string yaml = """
            services:
              torrent:
                image: torrent:latest
                network_mode: "service:vpn"
              proxy:
                image: proxy:latest
                network_mode: "service:vpn"
              vpn:
                image: vpn:latest
            """;

        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            yaml, new[] { "vpn" });

        dependents.Should().Equal("proxy", "torrent");
    }

    [Fact]
    public void GetDependentServices_InvalidYaml_ReturnsEmpty()
    {
        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            "services: [not: valid: yaml", new[] { "vpn" });

        dependents.Should().BeEmpty();
    }

    [Fact]
    public void GetDependentServices_NoServicesSection_ReturnsEmpty()
    {
        List<string> dependents = ComposeNamespaceDependencyHelper.GetDependentServices(
            "version: \"3\"", new[] { "vpn" });

        dependents.Should().BeEmpty();
    }
}
