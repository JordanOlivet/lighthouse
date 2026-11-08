using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests for one <see cref="AutoUpdateComposeBackgroundService"/> cycle: which projects are updated,
/// and the deferred retry of projects whose pull stayed rate limited.
/// </summary>
public class AutoUpdateComposeBackgroundServiceTests
{
    private readonly Mock<IComposeUpdateService> _composeUpdate = new();
    private readonly Mock<IComposeFileCacheService> _fileCache = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<ISelfUpdateService> _selfUpdate = new();
    private ComposeAutoUpdateReport? _report;

    public AutoUpdateComposeBackgroundServiceTests()
    {
        _selfUpdate.SetupGet(s => s.IsUpdateInProgress).Returns(false);
        _fileCache.Setup(f => f.GetFreshAsync()).ReturnsAsync(new List<DiscoveredComposeFile>());
        _composeUpdate
            .Setup(c => c.CheckProjectUpdatesAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, bool _, CancellationToken _) =>
                new ProjectUpdateCheckResponse(name, new List<ImageUpdateStatus>(), true, DateTime.UtcNow));
        _notifications
            .Setup(n => n.NotifyComposeAutoUpdateAsync(It.IsAny<ComposeAutoUpdateReport>(), It.IsAny<CancellationToken>()))
            .Callback((ComposeAutoUpdateReport r, CancellationToken _) => _report = r)
            .Returns(Task.CompletedTask);
    }

    // A positive delay enables the deferred retry (0 disables it); 1s keeps the tests fast while
    // still running the real code path.
    private const int ShortDelay = 1;

    private AutoUpdateComposeBackgroundService CreateService(int rateLimitRetryDelaySeconds = ShortDelay)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_composeUpdate.Object);
        services.AddSingleton(_fileCache.Object);
        services.AddSingleton(_notifications.Object);
        services.AddSingleton(_selfUpdate.Object);

        var options = new UpdateCheckOptions
        {
            AutoUpdateProjectDelaySeconds = 0,
            AutoUpdateRateLimitRetryDelaySeconds = rateLimitRetryDelaySeconds
        };

        return new AutoUpdateComposeBackgroundService(
            services.BuildServiceProvider(),
            Options.Create(options),
            new NullLogger<AutoUpdateComposeBackgroundService>());
    }

    private void SetupProjects(params string[] projectNames)
    {
        List<ProjectUpdateSummary> summaries = projectNames
            .Select(p => new ProjectUpdateSummary(p, 1, DateTime.UtcNow))
            .ToList();
        _composeUpdate
            .Setup(c => c.CheckAllProjectsUpdatesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckAllUpdatesResponse(summaries, summaries.Count, summaries.Count, summaries.Count, DateTime.UtcNow));
    }

    private void SetupUpdateResults(string projectName, params UpdateTriggerResponse[] results)
    {
        var queue = new Queue<UpdateTriggerResponse>(results);
        _composeUpdate
            .Setup(c => c.UpdateProjectAsync(projectName, null, true, true, true, 1, "system", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private static UpdateTriggerResponse Ok() => new(true, "ok", "op");
    private static UpdateTriggerResponse RateLimited() => new(false, "rate limit", "op", RateLimited: true);
    private static UpdateTriggerResponse Failed() => new(false, "boom", "op");

    private void VerifyUpdateCalls(string projectName, int times)
        => _composeUpdate.Verify(
            c => c.UpdateProjectAsync(projectName, null, true, true, true, 1, "system", It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    [Fact]
    public async Task RunCycle_AllSucceed_ReportsEachProjectOnce()
    {
        SetupProjects("a", "b");
        SetupUpdateResults("a", Ok());
        SetupUpdateResults("b", Ok());

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("a", 1);
        VerifyUpdateCalls("b", 1);
        _report!.Projects.Should().HaveCount(2).And.OnlyContain(p => p.Success);
    }

    [Fact]
    public async Task RunCycle_RateLimitedProject_IsRetriedAtEndOfCycleAndSucceeds()
    {
        SetupProjects("radarr", "searxng");
        SetupUpdateResults("radarr", RateLimited(), Ok());
        SetupUpdateResults("searxng", Ok());

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("radarr", 2);
        VerifyUpdateCalls("searxng", 1);
        // Reported once, with the final (successful) outcome, after the regular pass.
        _report!.Projects.Select(p => p.ProjectName).Should().Equal("searxng", "radarr");
        _report.Projects.Should().OnlyContain(p => p.Success);
    }

    [Fact]
    public async Task RunCycle_RateLimitedTwice_ReportedAsFailedAfterSingleRetry()
    {
        SetupProjects("radarr");
        SetupUpdateResults("radarr", RateLimited(), RateLimited());

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("radarr", 2);
        ProjectUpdateResult entry = _report!.Projects.Should().ContainSingle().Subject;
        entry.Success.Should().BeFalse();
        entry.Error.Should().Be("rate limit");
    }

    [Fact]
    public async Task RunCycle_NonRateLimitFailure_IsNotRetried()
    {
        SetupProjects("apprise");
        SetupUpdateResults("apprise", Failed());

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("apprise", 1);
        _report!.Projects.Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycle_DeferralDisabled_RateLimitedProjectFailsImmediately()
    {
        SetupProjects("radarr");
        SetupUpdateResults("radarr", RateLimited(), Ok());

        await CreateService(rateLimitRetryDelaySeconds: 0).RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("radarr", 1);
        _report!.Projects.Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycle_UpdateThrows_ReportedAsFailedWithoutRetry()
    {
        SetupProjects("broken");
        _composeUpdate
            .Setup(c => c.UpdateProjectAsync("broken", null, true, true, true, 1, "system", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker down"));

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        VerifyUpdateCalls("broken", 1);
        ProjectUpdateResult entry = _report!.Projects.Should().ContainSingle().Subject;
        entry.Success.Should().BeFalse();
        entry.Error.Should().Be("docker down");
    }

    [Fact]
    public async Task RunCycle_SkipsProjectsWithoutUpdates()
    {
        _composeUpdate
            .Setup(c => c.CheckAllProjectsUpdatesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckAllUpdatesResponse(
                new List<ProjectUpdateSummary> { new("idle", 0, DateTime.UtcNow) }, 1, 0, 0, DateTime.UtcNow));

        await CreateService().RunCycleAsync("test", CancellationToken.None);

        _composeUpdate.Verify(
            c => c.UpdateProjectAsync(It.IsAny<string>(), It.IsAny<List<string>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _report.Should().BeNull();
    }
}
