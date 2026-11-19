using Docker.DotNet.Models;
using FluentAssertions;
using Lighthouse.DTOs;
using Lighthouse.Services;
using Lighthouse.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests the one-shot re-run: only one-shots can be re-run, and their running
/// <c>restart: true</c> dependents join the same <c>up</c> (pure target computation in
/// <see cref="OneShotDetector.GetRerunTargets"/>; refusals in
/// <see cref="ComposeOperationService.RerunOneShotAsync"/>, which never reach the docker CLI).
/// </summary>
public class ComposeOperationServiceRerunTests
{
    private const string Project = "hermes";

    private readonly Mock<IDockerImageOperations> _dockerOps = new();
    private readonly List<ContainerListResponse> _containers = new()
    {
        Container("src-id", "agent-src", "exited"),
        Container("web-id", "webui", "running", "agent-src:service_completed_successfully:true"),
        Container("worker-id", "worker", "exited", "agent-src:service_completed_successfully:true"),
        Container("api-id", "api", "running"),
    };
    private readonly Dictionary<string, OneShotInfo> _oneShots = new()
    {
        ["src-id"] = new(OneShotRunStatus.Completed, 0, DateTime.UtcNow),
    };

    public ComposeOperationServiceRerunTests()
    {
        _dockerOps
            .Setup(d => d.ListContainersRawAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_containers);
        _dockerOps
            .Setup(d => d.ResolveOneShotsRawAsync(It.IsAny<IEnumerable<ContainerListResponse>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_oneShots);
    }

    private static ContainerListResponse Container(string id, string service, string state, string? dependsOn = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = Project,
            ["com.docker.compose.service"] = service,
        };
        if (dependsOn != null) labels["com.docker.compose.depends_on"] = dependsOn;
        return new ContainerListResponse { ID = id, State = state, Labels = labels };
    }

    private ComposeOperationService CreateService() => new(
        new DockerCommandExecutorService(NullLogger<DockerCommandExecutorService>.Instance),
        Mock.Of<IComposeDiscoveryService>(),
        Mock.Of<IComposeEnvFileResolver>(),
        Mock.Of<IComposeHostPathService>(),
        _dockerOps.Object,
        NullLogger<ComposeOperationService>.Instance);

    [Fact]
    public void GetRerunTargets_IncludesOnlyRunningRestartDependents()
    {
        // The stopped "worker" dependent is not started; "api" does not depend on the one-shot.
        OneShotDetector.GetRerunTargets(_containers, _oneShots, Project, "agent-src")
            .Should().Equal("agent-src", "webui");
    }

    [Fact]
    public void GetRerunTargets_RegularService_ReturnsNull()
    {
        OneShotDetector.GetRerunTargets(_containers, _oneShots, Project, "api").Should().BeNull();
    }

    [Fact]
    public void GetRerunTargets_OtherProject_ReturnsNull()
    {
        OneShotDetector.GetRerunTargets(_containers, _oneShots, "other", "agent-src").Should().BeNull();
    }

    [Fact]
    public async Task RerunOneShotAsync_RegularService_IsRefused()
    {
        OperationResult result = await CreateService().RerunOneShotAsync(Project, "/compose.yml", "api");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("one-shot");
    }

    [Theory]
    [InlineData("agent-src; rm -rf /")]
    [InlineData("-d")]
    [InlineData("")]
    public async Task RerunOneShotAsync_InvalidServiceName_IsRefusedBeforeDocker(string serviceName)
    {
        OperationResult result = await CreateService().RerunOneShotAsync(Project, "/compose.yml", serviceName);

        result.Success.Should().BeFalse();
        _dockerOps.Verify(d => d.ListContainersRawAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RerunOneShotAsync_NoComposeFile_IsRefused()
    {
        OperationResult result = await CreateService().RerunOneShotAsync(Project, null, "agent-src");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No compose file");
    }
}
