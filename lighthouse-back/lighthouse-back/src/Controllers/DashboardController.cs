using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Services;
using Lighthouse.src.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ContainerState = Lighthouse.src.Utils.EntityState;

namespace Lighthouse.Controllers;

/// <summary>
/// Dashboard statistics and overview endpoints
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : BaseController
{
    private readonly AppDbContext _context;
    private readonly DockerService _dockerService;
    private readonly IComposeDiscoveryService _composeDiscoveryService;
    private readonly ISelfFilterService _selfFilterService;
    private readonly ILogger<DashboardController> _logger;
    private readonly IProjectMatchingService _projectMatchingService;

    public DashboardController(
        AppDbContext context,
        DockerService dockerService,
        IComposeDiscoveryService composeDiscoveryService,
        ISelfFilterService selfFilterService,
        ILogger<DashboardController> logger,
        IProjectMatchingService projectMatchingService)
    {
        _context = context;
        _dockerService = dockerService;
        _composeDiscoveryService = composeDiscoveryService;
        _selfFilterService = selfFilterService;
        _logger = logger;
        _projectMatchingService = projectMatchingService;
    }

    /// <summary>
    /// Get aggregated dashboard statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ApiResponse<DashboardStatsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DashboardStatsDto>>> GetStats()
    {
        try
        {
            int? userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse.Fail<List<ComposeProjectDto>>("User not authenticated"));
            }

            // Get container stats (filtered to exclude self)
            List<ContainerDto> containers = await _dockerService.ListContainersAsync(showAll: true);

            // Filter out self containers
            string? selfProject = await _selfFilterService.GetSelfProjectNameAsync();
            string? selfContainerId = await _selfFilterService.GetSelfContainerIdAsync();
            if (selfProject != null || selfContainerId != null)
            {
                containers = containers.Where(c =>
                {
                    if (selfProject != null)
                    {
                        string? project = c.Labels?.GetValueOrDefault("com.docker.compose.project");
                        if (selfProject.Equals(project, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    if (selfContainerId != null)
                    {
                        if (c.Id.StartsWith(selfContainerId, StringComparison.OrdinalIgnoreCase)
                            || selfContainerId.StartsWith(c.Id, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    return true;
                }).ToList();
            }

            int runningContainers = containers.Count(c => c.State == ContainerState.Running.ToStateString());
            // A one-shot that completed successfully is idle by design, not stopped.
            int stoppedContainers = containers.Count(c =>
                c.State != ContainerState.Running.ToStateString() && !StateHelper.IsIdleOneShot(c));

            // Get compose project stats
            //List<ComposeProjectDto> projects = await _composeDiscoveryService.GetAllProjectsAsync();
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId.Value);
            int activeProjects = projects.Count(p => p.State == ContainerState.Running.ToStateString());

            // DEPRECATED: ComposeFiles table removed - return 0
            // Files are now discovered from Docker metadata
            int composeFilesCount = 0;

            // Get users count
            int usersCount = await _context.Users.CountAsync();
            int activeUsersCount = await _context.Users.CountAsync(u => u.IsEnabled);

            // Get recent activity count (last 24 hours) from recorded operations
            DateTime oneDayAgo = DateTime.UtcNow.AddDays(-1);
            int recentActivityCount = await _context.Operations
                .CountAsync(o => o.StartedAt >= oneDayAgo);

            DashboardStatsDto stats = new(
                TotalContainers: containers.Count,
                RunningContainers: runningContainers,
                StoppedContainers: stoppedContainers,
                TotalComposeProjects: projects.Count,
                ActiveProjects: activeProjects,
                ComposeFilesCount: composeFilesCount,
                UsersCount: usersCount,
                ActiveUsersCount: activeUsersCount,
                RecentActivityCount: recentActivityCount
            );

            return Ok(ApiResponse.Ok(stats, "Dashboard stats retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving dashboard stats");
            return StatusCode(500, ApiResponse.Fail<DashboardStatsDto>("Failed to retrieve dashboard stats"));
        }
    }

    /// <summary>
    /// Get recent activity log
    /// </summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(ApiResponse<List<ActivityDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<List<ActivityDto>>>> GetRecentActivity([FromQuery] int limit = 20)
    {
        try
        {
            List<Models.Operation> operations = await _context.Operations
                .Include(o => o.User)
                .OrderByDescending(o => o.StartedAt)
                .Take(limit)
                .ToListAsync();

            List<ActivityDto> activities = operations.Select(o => new ActivityDto(
                o.Id,
                o.UserId,
                o.User?.Username ?? "System",
                o.Type,
                o.ContainerId != null ? "container" : "compose",
                o.ContainerName ?? o.ProjectName,
                o.ErrorMessage ?? string.Empty,
                o.StartedAt,
                o.Status != Models.OperationStatus.Failed
            )).ToList();

            return Ok(ApiResponse.Ok(activities, "Recent activity retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving recent activity");
            return StatusCode(500, ApiResponse.Fail<List<ActivityDto>>("Failed to retrieve recent activity"));
        }
    }

    /// <summary>
    /// Get health status of all services
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(ApiResponse<HealthStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<HealthStatusDto>>> GetHealthStatus()
    {
        try
        {
            HealthStatusDto health = new();

            // Check database
            try
            {
                await _context.Database.CanConnectAsync();
                health.Database = new ServiceHealthDto(true, "Database is accessible");
            }
            catch (Exception ex)
            {
                health.Database = new ServiceHealthDto(false, $"Database error: {ex.Message}");
            }

            // Check Docker
            try
            {
                await _dockerService.ListContainersAsync(showAll: false);
                health.Docker = new ServiceHealthDto(true, "Docker daemon is accessible");
            }
            catch (Exception ex)
            {
                health.Docker = new ServiceHealthDto(false, $"Docker error: {ex.Message}");
            }

            // DEPRECATED: ComposePaths table removed - projects now discovered from Docker
            health.ComposePaths = new ServiceHealthDto(
                true,
                "File paths deprecated - projects discovered from Docker"
            );

            // Overall health
            health.Overall = health.Database.IsHealthy && health.Docker.IsHealthy;

            return Ok(ApiResponse.Ok(health, "Health status retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving health status");
            return StatusCode(500, ApiResponse.Fail<HealthStatusDto>("Failed to retrieve health status"));
        }
    }
}

// Dashboard DTOs
public record DashboardStatsDto(
    int TotalContainers,
    int RunningContainers,
    int StoppedContainers,
    int TotalComposeProjects,
    int ActiveProjects,
    int ComposeFilesCount,
    int UsersCount,
    int ActiveUsersCount,
    int RecentActivityCount
);

public record ActivityDto(
    int Id,
    int? UserId,
    string Username,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Details,
    DateTime Timestamp,
    bool Success
);

public record HealthStatusDto
{
    public bool Overall { get; set; }
    public ServiceHealthDto Database { get; set; } = new ServiceHealthDto(false, "Not checked");
    public ServiceHealthDto Docker { get; set; } = new ServiceHealthDto(false, "Not checked");
    public ServiceHealthDto ComposePaths { get; set; } = new ServiceHealthDto(false, "Not checked");
}

public record ServiceHealthDto(bool IsHealthy, string Message);
