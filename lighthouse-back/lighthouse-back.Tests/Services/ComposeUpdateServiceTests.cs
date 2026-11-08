using Docker.DotNet.Models;
using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests that <see cref="ComposeUpdateService"/> forwards the check/status concern to the injected
/// <see cref="IComposeUpdateChecker"/> after the PR6 split, and that the update orchestration keeps
/// the recreate phase out of reach of the caller's cancellation token (docker commands are mocked).
/// </summary>
public class ComposeUpdateServiceTests : IDisposable
{
    private const string Project = "proxy";
    private const string Service = "reverse-proxy";

    private readonly Mock<IComposeUpdateChecker> _checker = new();
    private readonly Mock<DockerCommandExecutorService> _executor =
        new(NullLogger<DockerCommandExecutorService>.Instance);
    private readonly Mock<IDockerImageOperations> _dockerOps = new();
    private readonly Mock<IComposeEnvFileResolver> _envFileResolver = new();
    private readonly Mock<IComposeHostPathService> _hostPathService = new();
    private readonly Mock<IOperationService> _operationService = new();
    private readonly Mock<IRegistryRateLimitGate> _rateLimitGate = new();
    private readonly Mock<IImageUpdateCacheService> _cacheService = new();
    private readonly string _composeDir;
    private readonly string _composeFile;

    public ComposeUpdateServiceTests()
    {
        _composeDir = Path.Combine(Path.GetTempPath(), $"lh-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_composeDir);
        _composeFile = Path.Combine(_composeDir, "compose.yml");
        File.WriteAllText(_composeFile, $"services:\n  {Service}:\n    image: traefik:v3\n");

        _checker.Setup(c => c.FindComposeFilePathAsync(Project)).ReturnsAsync(_composeFile);
        _hostPathService
            .Setup(h => h.GetRelativeMountConflictAsync(Project, _composeFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _hostPathService
            .Setup(h => h.ToExecutionPathAsync(_composeFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_composeFile);
        _envFileResolver
            .Setup(e => e.BuildEnvFileArgsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        // The updated service has a running container, so the recreate phase runs `compose up`.
        _dockerOps
            .Setup(d => d.ListContainersRawAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ContainerListResponse>
            {
                new()
                {
                    State = "running",
                    Labels = new Dictionary<string, string>
                    {
                        ["com.docker.compose.project"] = Project,
                        ["com.docker.compose.service"] = Service,
                    },
                },
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_composeDir, recursive: true); } catch { /* best effort */ }
    }

    private ComposeUpdateService CreateService() => new(
        _checker.Object,
        _cacheService.Object,
        _executor.Object,
        _dockerOps.Object,
        _envFileResolver.Object,
        _hostPathService.Object,
        new DockerPullProgressParser(NullLogger<DockerPullProgressParser>.Instance),
        _operationService.Object,
        _rateLimitGate.Object,
        Options.Create(new UpdateCheckOptions()),
        new NullLogger<ComposeUpdateService>());

    /// <summary>
    /// Mocks one docker invocation whose arguments contain <paramref name="argsFragment"/>.
    /// Like the real executor, a cancelled token makes the call throw.
    /// </summary>
    private void SetupDocker(string argsFragment, Action<CancellationToken>? onRun = null)
    {
        _executor
            .Setup(e => e.ExecuteWithStreamingAsync(
                "docker",
                It.Is<string>(a => a.Contains(argsFragment)),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns((string _, string _, Action<string>? _, Action<string>? _, CancellationToken token, string? _) =>
            {
                onRun?.Invoke(token);
                token.ThrowIfCancellationRequested();
                return Task.FromResult((0, string.Empty, string.Empty));
            });
    }

    private Task<UpdateTriggerResponse> UpdateAsync(CancellationToken ct) =>
        CreateService().UpdateProjectAsync(
            Project,
            new List<string> { Service },
            updateAll: false,
            restartFullProject: false,
            restartAfterUpdate: true,
            userId: 1,
            ipAddress: "127.0.0.1",
            ct);

    [Fact]
    public async Task UpdateProjectAsync_ClientDisconnectsDuringRecreate_RecreateStillCompletes()
    {
        // Updating the reverse proxy that fronts the UI drops the HTTP connection as soon as
        // compose stops the old container, which fires the request token mid-recreate.
        using var requestAborted = new CancellationTokenSource();
        CancellationToken recreateToken = default;
        SetupDocker(" pull ");
        SetupDocker(" up -d --force-recreate ", token =>
        {
            requestAborted.Cancel();
            recreateToken = token;
        });

        UpdateTriggerResponse result = await UpdateAsync(requestAborted.Token);

        result.Success.Should().BeTrue();
        recreateToken.CanBeCanceled.Should().BeFalse();
        _operationService.Verify(
            o => o.UpdateOperationStatusAsync(It.IsAny<string>(), OperationStatus.Completed, 100, null),
            Times.Once);
    }

    [Fact]
    public async Task UpdateProjectAsync_CancelledDuringPull_NeverRecreatesContainers()
    {
        using var requestAborted = new CancellationTokenSource();
        SetupDocker(" pull ", _ => requestAborted.Cancel());
        SetupDocker(" up -d --force-recreate ");

        UpdateTriggerResponse result = await UpdateAsync(requestAborted.Token);

        result.Success.Should().BeFalse();
        _executor.Verify(
            e => e.ExecuteWithStreamingAsync(
                "docker",
                It.Is<string>(a => a.Contains(" up ") || a.Contains(" create ")),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()),
            Times.Never);
    }

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
