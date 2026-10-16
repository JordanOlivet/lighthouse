using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests that <see cref="ComposeUpdateService"/> forwards the check/status concern to the injected
/// <see cref="IComposeUpdateChecker"/> after the PR6 split. The update-orchestration methods drive
/// real docker side-effects and are not unit-tested here.
/// </summary>
public class ComposeUpdateServiceTests
{
    private readonly Mock<IComposeUpdateChecker> _checker = new();

    private ComposeUpdateService CreateService() => new(
        _checker.Object,
        cacheService: null!,
        dockerExecutor: null!,
        dockerOps: null!,
        envFileResolver: null!,
        hostPathService: null!,
        progressParser: null!,
        operationServiceDb: null!,
        rateLimitGate: null!,
        Options.Create(new UpdateCheckOptions()),
        new NullLogger<ComposeUpdateService>());

    [Fact]
    public async Task CheckProjectUpdatesAsync_DelegatesToChecker()
    {
        var response = new ProjectUpdateCheckResponse("proj", new List<ImageUpdateStatus>(), false, DateTime.UtcNow);
        _checker.Setup(c => c.CheckProjectUpdatesAsync("proj", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        (await CreateService().CheckProjectUpdatesAsync("proj", true)).Should().BeSameAs(response);
    }

    [Fact]
    public void GetGlobalUpdateStatus_DelegatesToChecker()
    {
        var summaries = new List<ProjectUpdateSummary> { new("proj", 1, DateTime.UtcNow) };
        _checker.Setup(c => c.GetGlobalUpdateStatus()).Returns(summaries);

        CreateService().GetGlobalUpdateStatus().Should().BeSameAs(summaries);
    }

    [Fact]
    public void ClearCache_DelegatesToChecker()
    {
        CreateService().ClearCache();
        _checker.Verify(c => c.ClearCache(), Times.Once);
    }

    [Fact]
    public async Task CheckAllProjectsUpdatesAsync_DelegatesToChecker()
    {
        var response = new CheckAllUpdatesResponse(new List<ProjectUpdateSummary>(), 2, 1, 1, DateTime.UtcNow);
        _checker.Setup(c => c.CheckAllProjectsUpdatesAsync(7, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        (await CreateService().CheckAllProjectsUpdatesAsync(7)).Should().BeSameAs(response);
    }
}
