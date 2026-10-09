using Lighthouse.Controllers;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace Lighthouse.Tests.Controllers;

public class ComposeControllerTests
{
    private readonly Mock<IComposeDiscoveryService> _discovery = new();
    private readonly Mock<IComposeOperationService> _composeOp = new();
    private readonly Mock<IOperationService> _legacyOp = new();
    private readonly Mock<IPermissionService> _permission = new();
    private readonly Mock<IProjectMatchingService> _matching = new();
    private readonly Mock<IComposeFileCacheService> _fileCache = new();
    private readonly Mock<IImageUpdateCacheService> _imageCache = new();
    private readonly Mock<ISelfFilterService> _selfFilter = new();
    private readonly Mock<Lighthouse.Services.LogStreaming.IContainerLogService> _containerLogService = new();

    private ComposeController Build(bool authenticated = true)
    {
        // Sensible permissive defaults; individual tests override.
        _selfFilter.Setup(s => s.IsSelfProjectAsync(It.IsAny<string>())).ReturnsAsync(false);
        _permission.Setup(p => p.HasPermissionAsync(
                It.IsAny<int>(), It.IsAny<ResourceType>(), It.IsAny<string>(), It.IsAny<PermissionFlags>()))
            .ReturnsAsync(true);
        _legacyOp.Setup(o => o.CreateOperationAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new Operation { OperationId = "op1", Type = OperationType.ComposeUp, Status = OperationStatus.Pending });

        var coordinator = new Lighthouse.Services.LogStreaming.ComposeLogStreamCoordinator(
            _containerLogService.Object,
            new Lighthouse.Services.DockerEventBus(NullLogger<Lighthouse.Services.DockerEventBus>.Instance),
            NullLogger<Lighthouse.Services.LogStreaming.ComposeLogStreamCoordinator>.Instance);

        var controller = new ComposeController(
            _discovery.Object, _composeOp.Object, _legacyOp.Object,
            _permission.Object, NullLogger<ComposeController>.Instance, _matching.Object,
            _fileCache.Object, _imageCache.Object, _selfFilter.Object,
            _containerLogService.Object, coordinator);

        // Set the current user on the controller context (BaseController reads claims).
        var identity = authenticated
            ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test")
            : new ClaimsIdentity();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static int? StatusOf(ActionResult<ApiResponse<ComposeOperationResponse>> result) =>
        (result.Result as ObjectResult)?.StatusCode;

    private static ComposeProjectDto Project(string name, bool hasComposeFile) =>
        new(name, $"/compose/{name}", "down", new List<ComposeServiceDto>(), new List<string>(), null,
            ComposeFilePath: hasComposeFile ? $"/compose/{name}/docker-compose.yml" : null,
            HasComposeFile: hasComposeFile);

    [Fact]
    public async Task UpProject_SelfProject_Returns403()
    {
        var controller = Build();
        _selfFilter.Setup(s => s.IsSelfProjectAsync("self")).ReturnsAsync(true);

        var result = await controller.UpProject("self", new ComposeUpRequest());

        StatusOf(result).Should().Be(403);
    }

    [Fact]
    public async Task UpProject_NoPermission_Returns403()
    {
        var controller = Build();
        _permission.Setup(p => p.HasPermissionAsync(
                It.IsAny<int>(), ResourceType.ComposeProject, "proj", PermissionFlags.Start))
            .ReturnsAsync(false);

        var result = await controller.UpProject("proj", new ComposeUpRequest());

        StatusOf(result).Should().Be(403);
    }

    [Fact]
    public async Task UpProject_Unauthenticated_Returns401()
    {
        var controller = Build(authenticated: false);

        var result = await controller.UpProject("proj", new ComposeUpRequest());

        StatusOf(result).Should().Be(401);
    }

    [Fact]
    public async Task UpProject_NoComposeFile_Returns400()
    {
        var controller = Build();
        _matching.Setup(m => m.GetUnifiedProjectListAsync(1))
            .ReturnsAsync(new List<ComposeProjectDto> { Project("proj", hasComposeFile: false) });

        var result = await controller.UpProject("proj", new ComposeUpRequest());

        StatusOf(result).Should().Be(400);
    }

    [Fact]
    public async Task UpProject_Success_Returns200()
    {
        var controller = Build();
        _matching.Setup(m => m.GetUnifiedProjectListAsync(1))
            .ReturnsAsync(new List<ComposeProjectDto> { Project("proj", hasComposeFile: true) });
        _composeOp.Setup(o => o.UpAsync("proj", It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Success = true, Message = "started", Output = "", Error = null });

        var result = await controller.UpProject("proj", new ComposeUpRequest());

        (result.Result as OkObjectResult)?.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpProject_ForwardsForceRecreate(bool forceRecreate)
    {
        var controller = Build();
        _matching.Setup(m => m.GetUnifiedProjectListAsync(1))
            .ReturnsAsync(new List<ComposeProjectDto> { Project("proj", hasComposeFile: true) });
        _composeOp.Setup(o => o.UpAsync("proj", It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Success = true, Message = "started", Output = "", Error = null });

        await controller.UpProject("proj", new ComposeUpRequest(ForceRecreate: forceRecreate));

        _composeOp.Verify(o => o.UpAsync("proj", It.IsAny<string?>(), false, forceRecreate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownProject_NoPermission_Returns403()
    {
        var controller = Build();
        _permission.Setup(p => p.HasPermissionAsync(
                It.IsAny<int>(), ResourceType.ComposeProject, "proj", PermissionFlags.Stop))
            .ReturnsAsync(false);

        var result = await controller.DownProject("proj", new ComposeDownRequest());

        StatusOf(result).Should().Be(403);
    }

    // ---- log history endpoint ----

    private static int? StatusOfLogs(ActionResult<ApiResponse<LogPageDto>> result) =>
        (result.Result as ObjectResult)?.StatusCode;

    [Fact]
    public async Task LogHistory_SelfProject_Returns403()
    {
        var controller = Build();
        _selfFilter.Setup(s => s.IsSelfProjectAsync("self")).ReturnsAsync(true);

        var result = await controller.GetProjectLogHistory("self");

        StatusOfLogs(result).Should().Be(403);
    }

    [Fact]
    public async Task LogHistory_Unauthenticated_Returns401()
    {
        var controller = Build(authenticated: false);

        var result = await controller.GetProjectLogHistory("proj");

        StatusOfLogs(result).Should().Be(401);
    }

    [Fact]
    public async Task LogHistory_NoLogsPermission_Returns403()
    {
        var controller = Build();
        _permission.Setup(p => p.HasPermissionAsync(
                It.IsAny<int>(), ResourceType.ComposeProject, "proj", PermissionFlags.Logs))
            .ReturnsAsync(false);

        var result = await controller.GetProjectLogHistory("proj");

        StatusOfLogs(result).Should().Be(403);
    }

    [Fact]
    public async Task LogHistory_Success_MergesContainers()
    {
        var controller = Build();
        _containerLogService.Setup(l => l.ListProjectContainerIdsAsync("proj", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "a", "b" });
        _containerLogService.Setup(l => l.GetLogSourceAsync("a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Lighthouse.Services.LogStreaming.ContainerLogSource("a", "a", "proj", "web", false));
        _containerLogService.Setup(l => l.GetLogSourceAsync("b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Lighthouse.Services.LogStreaming.ContainerLogSource("b", "b", "proj", "db", false));
        _containerLogService.Setup(l => l.GetHistoryAsync(
                It.Is<Lighthouse.Services.LogStreaming.ContainerLogSource>(s => s.Id == "a"), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogPageDto(new List<LogEntryDto> { new("2026-07-04T12:00:00.000000000Z", "a", "a", "web", "stdout", "a1") }, false));
        _containerLogService.Setup(l => l.GetHistoryAsync(
                It.Is<Lighthouse.Services.LogStreaming.ContainerLogSource>(s => s.Id == "b"), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogPageDto(new List<LogEntryDto> { new("2026-07-04T12:00:01.000000000Z", "b", "b", "db", "stdout", "b1") }, false));

        var result = await controller.GetProjectLogHistory("proj");

        (result.Result as OkObjectResult)?.StatusCode.Should().Be(200);
        var page = ((result.Result as OkObjectResult)?.Value as ApiResponse<LogPageDto>)?.Data;
        page!.Entries.Select(e => e.Message).Should().Equal("a1", "b1");
    }

    [Fact]
    public async Task LogHistory_ServiceFilter_ExcludesOtherServices()
    {
        var controller = Build();
        _containerLogService.Setup(l => l.ListProjectContainerIdsAsync("proj", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "a", "b" });
        _containerLogService.Setup(l => l.GetLogSourceAsync("a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Lighthouse.Services.LogStreaming.ContainerLogSource("a", "a", "proj", "web", false));
        _containerLogService.Setup(l => l.GetLogSourceAsync("b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Lighthouse.Services.LogStreaming.ContainerLogSource("b", "b", "proj", "db", false));
        _containerLogService.Setup(l => l.GetHistoryAsync(
                It.Is<Lighthouse.Services.LogStreaming.ContainerLogSource>(s => s.Id == "a"), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogPageDto(new List<LogEntryDto> { new("2026-07-04T12:00:00.000000000Z", "a", "a", "web", "stdout", "a1") }, false));

        var result = await controller.GetProjectLogHistory("proj", services: "web");

        var page = ((result.Result as OkObjectResult)?.Value as ApiResponse<LogPageDto>)?.Data;
        page!.Entries.Select(e => e.Message).Should().Equal("a1");
        // 'db' service filtered out → its history is never fetched
        _containerLogService.Verify(l => l.GetHistoryAsync(
            It.Is<Lighthouse.Services.LogStreaming.ContainerLogSource>(s => s.Id == "b"),
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
