using Docker.DotNet;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.LogStreaming;
using Lighthouse.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Lighthouse.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ComposeController : BaseController
{
    private readonly IComposeDiscoveryService _discoveryService;
    private readonly IComposeOperationService _operationService;
    private readonly IOperationService _legacyOperationService;
    private readonly IPermissionService _permissionService;
    private readonly ILogger<ComposeController> _logger;
    private readonly IProjectMatchingService _projectMatchingService;
    private readonly IComposeFileCacheService _composeFileCacheService;
    private readonly IImageUpdateCacheService _imageUpdateCacheService;
    private readonly ISelfFilterService _selfFilterService;
    private readonly IContainerLogService _containerLogService;
    private readonly ComposeLogStreamCoordinator _logStreamCoordinator;

    public ComposeController(
        IComposeDiscoveryService discoveryService,
        IComposeOperationService operationService,
        IOperationService legacyOperationService,
        IPermissionService permissionService,
        ILogger<ComposeController> logger,
        IProjectMatchingService projectMatchingService,
        IComposeFileCacheService composeFileCacheService,
        IImageUpdateCacheService imageUpdateCacheService,
        ISelfFilterService selfFilterService,
        IContainerLogService containerLogService,
        ComposeLogStreamCoordinator logStreamCoordinator)
    {
        _discoveryService = discoveryService;
        _operationService = operationService;
        _legacyOperationService = legacyOperationService;
        _permissionService = permissionService;
        _logger = logger;
        _projectMatchingService = projectMatchingService;
        _composeFileCacheService = composeFileCacheService;
        _imageUpdateCacheService = imageUpdateCacheService;
        _selfFilterService = selfFilterService;
        _containerLogService = containerLogService;
        _logStreamCoordinator = logStreamCoordinator;
    }

    // ============================================
    // Compose Projects Endpoints
    // ============================================

    /// <summary>
    /// Lists all compose projects (unified view of Docker projects and discovered compose files)
    /// </summary>
    /// <remarks>
    /// Returns a unified list that includes:
    /// - Running Docker projects (with or without compose files)
    /// - Not-started projects (compose files without Docker containers)
    /// - Enriched with file paths, available actions, and warnings
    /// </remarks>
    [HttpGet("projects")]
    public async Task<ActionResult<ApiResponse<List<ComposeProjectDto>>>> ListProjects(
        [FromQuery] bool refresh = false,
        [FromQuery] bool refreshState = false)
    {
        try
        {
            // Get user ID for permission filtering
            int? userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse.Fail<List<ComposeProjectDto>>("User not authenticated"));
            }

            // Invalidate caches based on refresh type:
            // - refresh: invalidate both caches (use when files might have changed)
            // - refreshState: only invalidate Docker cache (use for container state changes - much faster)
            if (refresh)
            {
                _composeFileCacheService.Invalidate();  // Compose file cache (triggers slow filesystem scan)
                _discoveryService.InvalidateCache();  // Docker projects cache
                _imageUpdateCacheService.InvalidateAll(); // Docker image cache
            }
            else if (refreshState)
            {
                _discoveryService.InvalidateCache();  // Only Docker projects cache (fast)
            }

            // Get unified project list from matching service (includes permission filtering)
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId.Value);

            _logger.LogDebug("User {UserId} listed {Count} compose projects", userId.Value, projects.Count);

            return Ok(ApiResponse.Ok(projects));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing compose projects");
            return StatusCode(500, ApiResponse.Fail<List<ComposeProjectDto>>("Error listing projects", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Starts a compose project (docker compose up)
    /// </summary>
    [HttpPost("projects/{projectName}/up")]
    public async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> UpProject(
        string projectName,
        [FromBody] ComposeUpRequest? request)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            var (userId, error) = await AuthorizeProjectOperationAsync(projectName, PermissionFlags.Start, "start");
            if (error != null) return error;

            // 'up' requires a compose file, so resolve the project first.
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId);
            ComposeProjectDto? project = projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (project == null)
            {
                return NotFound(ApiResponse.Fail<ComposeOperationResponse>("Project not found"));
            }

            if (!project.HasComposeFile)
            {
                return BadRequest(ApiResponse.Fail<ComposeOperationResponse>(
                    $"Cannot execute 'up' command: No compose file found for project '{projectName}'. " +
                    "This command requires a compose file to function.",
                    "COMPOSE_FILE_REQUIRED"
                ));
            }

            OperationResult result = await _operationService.UpAsync(
                projectName, project.ComposeFilePath, request?.Build ?? false, request?.ForceRecreate ?? false);

            return await FinalizeOperationAsync(
                OperationType.ComposeUp, userId, projectName, project.Path,
                result, ComposeOutputHelper.BuildLogs(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeOperationResponse>("Error starting project", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Stops a compose project (docker compose down)
    /// </summary>
    [HttpPost("projects/{projectName}/down")]
    public async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> DownProject(
        string projectName,
        [FromBody] ComposeDownRequest? request)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            var (userId, error) = await AuthorizeProjectOperationAsync(projectName, PermissionFlags.Stop, "stop");
            if (error != null) return error;

            string? projectPath = await ResolveProjectPathAsync(projectName, userId);
            OperationResult result = await _operationService.DownAsync(projectName, request?.RemoveVolumes ?? false);

            return await FinalizeOperationAsync(
                OperationType.ComposeDown, userId, projectName, projectPath,
                result, ComposeOutputHelper.BuildLogs(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeOperationResponse>("Error stopping project", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Get available compose file templates
    /// </summary>
    [HttpGet("templates")]
    public ActionResult<ApiResponse<List<ComposeTemplateDto>>> GetTemplates()
    {
        try
        {
            List<ComposeTemplateDto> templates = new()
            {
                new ComposeTemplateDto(
                    "wordpress",
                    "WordPress + MySQL",
                    "Complete WordPress installation with MySQL database",
                    @"version: '3.8'

services:
  wordpress:
    image: wordpress:latest
    ports:
      - ""80:80""
    environment:
      WORDPRESS_DB_HOST: db
      WORDPRESS_DB_USER: wordpress
      WORDPRESS_DB_PASSWORD: wordpress
      WORDPRESS_DB_NAME: wordpress
    volumes:
      - wordpress_data:/var/www/html
    depends_on:
      - db

  db:
    image: mysql:8.0
    environment:
      MYSQL_DATABASE: wordpress
      MYSQL_USER: wordpress
      MYSQL_PASSWORD: wordpress
      MYSQL_RANDOM_ROOT_PASSWORD: '1'
    volumes:
      - db_data:/var/lib/mysql

volumes:
  wordpress_data:
  db_data:"
                ),
                new ComposeTemplateDto(
                    "nginx-php",
                    "Nginx + PHP-FPM",
                    "Web server with Nginx and PHP-FPM",
                    @"version: '3.8'

services:
  nginx:
    image: nginx:alpine
    ports:
      - ""80:80""
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./www:/var/www/html
    depends_on:
      - php

  php:
    image: php:8.2-fpm
    volumes:
      - ./www:/var/www/html"
                ),
                new ComposeTemplateDto(
                    "postgres-redis",
                    "PostgreSQL + Redis",
                    "PostgreSQL database with Redis cache",
                    @"version: '3.8'

services:
  postgres:
    image: postgres:15
    environment:
      POSTGRES_DB: mydb
      POSTGRES_USER: myuser
      POSTGRES_PASSWORD: mypassword
    ports:
      - ""5432:5432""
    volumes:
      - postgres_data:/var/lib/postgresql/data

  redis:
    image: redis:7-alpine
    ports:
      - ""6379:6379""
    volumes:
      - redis_data:/data

volumes:
  postgres_data:
  redis_data:"
                ),
                new ComposeTemplateDto(
                    "traefik",
                    "Traefik Reverse Proxy",
                    "Traefik reverse proxy with Let's Encrypt",
                    @"version: '3.8'

services:
  traefik:
    image: traefik:v2.10
    command:
      - --api.dashboard=true
      - --providers.docker=true
      - --entrypoints.web.address=:80
      - --entrypoints.websecure.address=:443
    ports:
      - ""80:80""
      - ""443:443""
      - ""8080:8080""
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - ./acme.json:/acme.json
    labels:
      - traefik.enable=true"
                ),
                new ComposeTemplateDto(
                    "monitoring",
                    "Prometheus + Grafana",
                    "Monitoring stack with Prometheus and Grafana",
                    @"version: '3.8'

services:
  prometheus:
    image: prom/prometheus:latest
    ports:
      - ""9090:9090""
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus_data:/prometheus

  grafana:
    image: grafana/grafana:latest
    ports:
      - ""3000:3000""
    environment:
      GF_SECURITY_ADMIN_PASSWORD: admin
    volumes:
      - grafana_data:/var/lib/grafana
    depends_on:
      - prometheus

volumes:
  prometheus_data:
  grafana_data:"
                )
            };

            return Ok(ApiResponse.Ok(templates, "Templates retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting compose templates");
            return StatusCode(500, ApiResponse.Fail<List<ComposeTemplateDto>>("Error getting templates", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Get detailed information about a specific compose project
    /// </summary>
    [HttpGet("projects/{projectName}")]
    public async Task<ActionResult<ApiResponse<ComposeProjectDto>>> GetProjectDetails(string projectName)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            int? userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse.Fail<ComposeProjectDto>("User not authenticated"));
            }

            // Get project from unified list (includes file path resolution, permissions, and enrichment)
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId.Value);
            ComposeProjectDto? project = projects.FirstOrDefault(p =>
                p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (project == null)
            {
                return NotFound(ApiResponse.Fail<ComposeProjectDto>("Project not found or access denied", "PROJECT_NOT_FOUND"));
            }

            // Surface whether this user may edit the compose file so the detail page can gate the
            // edit affordance. Only projects that actually have a file are editable.
            if (project.HasComposeFile)
            {
                bool canEdit = await _permissionService.HasPermissionAsync(
                    userId.Value, ResourceType.ComposeProject, project.Name, PermissionFlags.Edit);
                project = project with { CanEdit = canEdit };
            }


            return Ok(ApiResponse.Ok(project, "Project details retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting project details for: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeProjectDto>("Error getting project details", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Get parsed compose file details with structured information (networks, volumes, env vars, labels, etc.)
    /// </summary>
    [HttpGet("projects/{projectName}/parsed")]
    public async Task<ActionResult<ApiResponse<ComposeFileDetailsDto>>> GetProjectParsedDetails(string projectName)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            // Check authentication
            int? userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse.Fail<ComposeFileDetailsDto>("User not authenticated"));
            }

            // Check View permission
            bool hasPermission = await _permissionService.HasPermissionAsync(
                userId.Value,
                ResourceType.ComposeProject,
                projectName,
                PermissionFlags.View);

            if (!hasPermission)
            {
                return StatusCode(403, ApiResponse.Fail<ComposeFileDetailsDto>(
                    "You don't have permission to view this compose project",
                    "PERMISSION_DENIED"));
            }

            // Get project from unified list (includes file path resolution)
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId.Value);
            ComposeProjectDto? project = projects.FirstOrDefault(p =>
                p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (project == null)
            {
                return NotFound(ApiResponse.Fail<ComposeFileDetailsDto>("Project not found", "PROJECT_NOT_FOUND"));
            }

            // Check if we have a compose file path
            if (string.IsNullOrEmpty(project.ComposeFilePath))
            {
                return NotFound(ApiResponse.Fail<ComposeFileDetailsDto>(
                    "No compose file found for this project. The file may have been moved or deleted.",
                    "FILE_NOT_FOUND"));
            }

            // Read file content directly from the resolved path
            string composeFile = project.ComposeFilePath;
            if (!System.IO.File.Exists(composeFile))
            {
                _logger.LogWarning("Compose file not found at path: {Path}", composeFile);
                return NotFound(ApiResponse.Fail<ComposeFileDetailsDto>(
                    $"Compose file not found at: {composeFile}",
                    "FILE_NOT_FOUND"));
            }

            string content;
            try
            {
                content = await System.IO.File.ReadAllTextAsync(composeFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading compose file: {Path}", composeFile);
                return BadRequest(ApiResponse.Fail<ComposeFileDetailsDto>(
                    $"Error reading compose file: {ex.Message}", "READ_ERROR"));
            }

            // Parse YAML
            try
            {
                Dictionary<string, object>? composeData = YamlParserHelper.Deserialize(content);

                if (composeData == null)
                {
                    return BadRequest(ApiResponse.Fail<ComposeFileDetailsDto>("Invalid compose file format", "INVALID_FORMAT"));
                }

                // Extract version
                string? version = composeData.ContainsKey("version")
                    ? composeData["version"]?.ToString()
                    : null;

                // Extract services
                Dictionary<string, ServiceDetailsDto> servicesDict = new();
                if (composeData.ContainsKey("services") && composeData["services"] is Dictionary<object, object> services)
                {
                    foreach (KeyValuePair<object, object> svcEntry in services)
                    {
                        string serviceName = svcEntry.Key.ToString() ?? "unknown";
                        Dictionary<object, object>? svcData = svcEntry.Value as Dictionary<object, object>;

                        if (svcData != null)
                        {
                            servicesDict[serviceName] = new ServiceDetailsDto(
                                Name: serviceName,
                                Image: svcData.ContainsKey("image") ? svcData["image"]?.ToString() : null,
                                Build: svcData.ContainsKey("build") ? svcData["build"]?.ToString() : null,
                                Ports: YamlParserHelper.ExtractStringList(svcData, "ports"),
                                Environment: YamlParserHelper.ExtractEnvironment(svcData),
                                Labels: YamlParserHelper.ExtractStringDictionary(svcData, "labels"),
                                Volumes: YamlParserHelper.ExtractStringList(svcData, "volumes"),
                                DependsOn: YamlParserHelper.ExtractStringList(svcData, "depends_on"),
                                Restart: svcData.ContainsKey("restart") ? svcData["restart"]?.ToString() : null,
                                Networks: YamlParserHelper.ExtractStringDictionary(svcData, "networks")
                            );
                        }
                    }
                }

                // Extract networks
                Dictionary<string, NetworkDetailsDto>? networksDict = null;
                if (composeData.ContainsKey("networks") && composeData["networks"] is Dictionary<object, object> networks)
                {
                    networksDict = new Dictionary<string, NetworkDetailsDto>();
                    foreach (KeyValuePair<object, object> netEntry in networks)
                    {
                        string networkName = netEntry.Key.ToString() ?? "unknown";
                        Dictionary<object, object>? netData = netEntry.Value as Dictionary<object, object>;

                        if (netData != null)
                        {
                            networksDict[networkName] = new NetworkDetailsDto(
                                Name: networkName,
                                Driver: netData.ContainsKey("driver") ? netData["driver"]?.ToString() : null,
                                External: netData.ContainsKey("external") ? Convert.ToBoolean(netData["external"]) : null,
                                DriverOpts: YamlParserHelper.ExtractObjectDictionary(netData, "driver_opts"),
                                Labels: YamlParserHelper.ExtractStringDictionary(netData, "labels")
                            );
                        }
                    }
                }

                // Extract volumes
                Dictionary<string, VolumeDetailsDto>? volumesDict = null;
                if (composeData.ContainsKey("volumes") && composeData["volumes"] is Dictionary<object, object> volumes)
                {
                    volumesDict = new Dictionary<string, VolumeDetailsDto>();
                    foreach (KeyValuePair<object, object> volEntry in volumes)
                    {
                        string volumeName = volEntry.Key.ToString() ?? "unknown";
                        Dictionary<object, object>? volData = volEntry.Value as Dictionary<object, object>;

                        if (volData != null)
                        {
                            volumesDict[volumeName] = new VolumeDetailsDto(
                                Name: volumeName,
                                Driver: volData.ContainsKey("driver") ? volData["driver"]?.ToString() : null,
                                External: volData.ContainsKey("external") ? Convert.ToBoolean(volData["external"]) : null,
                                DriverOpts: YamlParserHelper.ExtractObjectDictionary(volData, "driver_opts"),
                                Labels: YamlParserHelper.ExtractStringDictionary(volData, "labels")
                            );
                        }
                    }
                }

                ComposeFileDetailsDto result = new(
                    ProjectName: projectName,
                    Version: version,
                    Services: servicesDict,
                    Networks: networksDict,
                    Volumes: volumesDict
                );


                return Ok(ApiResponse.Ok(result, "Parsed compose file details retrieved successfully"));
            }
            catch (YamlDotNet.Core.YamlException yamlEx)
            {
                _logger.LogWarning(yamlEx, "Error parsing YAML for project: {ProjectName}", projectName);
                return BadRequest(ApiResponse.Fail<ComposeFileDetailsDto>(
                    $"Error parsing YAML: {yamlEx.Message}", "YAML_PARSE_ERROR"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting parsed details for project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeFileDetailsDto>("Error getting parsed details", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Resolves the project path from the project name for operation tracking.
    /// </summary>
    private async Task<string?> ResolveProjectPathAsync(string projectName, int userId)
    {
        try
        {
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(userId);
            return projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the guards shared by every project lifecycle action: self-protection,
    /// authentication and permission. Returns the resolved user id on success, or an
    /// error result to return directly.
    /// </summary>
    private async Task<(int UserId, ActionResult<ApiResponse<ComposeOperationResponse>>? Error)> AuthorizeProjectOperationAsync(
        string projectName, PermissionFlags permission, string permissionVerb)
    {
        if (await _selfFilterService.IsSelfProjectAsync(projectName))
        {
            return (0, StatusCode(403, ApiResponse.Fail<ComposeOperationResponse>(
                "This project belongs to the application itself and cannot be modified",
                "SELF_PROJECT_PROTECTED")));
        }

        int? userId = GetCurrentUserId();
        if (!userId.HasValue)
        {
            return (0, Unauthorized(ApiResponse.Fail<ComposeOperationResponse>("User not authenticated")));
        }

        bool hasPermission = await _permissionService.HasPermissionAsync(
            userId.Value, ResourceType.ComposeProject, projectName, permission);
        if (!hasPermission)
        {
            return (0, StatusCode(403, ApiResponse.Fail<ComposeOperationResponse>(
                $"You don't have permission to {permissionVerb} this compose project",
                "PERMISSION_DENIED")));
        }

        return (userId.Value, null);
    }

    /// <summary>
    /// Guards the read-only log endpoints: self-protection, authentication and the
    /// project-level Logs permission. Generic over the endpoint's response type.
    /// </summary>
    private async Task<(int UserId, ActionResult<ApiResponse<T>>? Error)> AuthorizeProjectLogsAsync<T>(string projectName)
    {
        if (await _selfFilterService.IsSelfProjectAsync(projectName))
        {
            return (0, StatusCode(403, ApiResponse.Fail<T>(
                "This project belongs to the application itself and cannot be accessed",
                "SELF_PROJECT_PROTECTED")));
        }

        int? userId = GetCurrentUserId();
        if (!userId.HasValue)
        {
            return (0, Unauthorized(ApiResponse.Fail<T>("User not authenticated")));
        }

        bool hasPermission = await _permissionService.HasPermissionAsync(
            userId.Value, ResourceType.ComposeProject, projectName, PermissionFlags.Logs);
        if (!hasPermission)
        {
            return (0, StatusCode(403, ApiResponse.Fail<T>(
                "You don't have permission to view logs for this compose project",
                "PERMISSION_DENIED")));
        }

        return (userId.Value, null);
    }

    private static HashSet<string>? ParseServiceFilter(string? services) =>
        string.IsNullOrWhiteSpace(services)
            ? null
            : services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns one merged, timestamp-ordered page of historical logs across all of the
    /// project's containers (including stopped ones), for infinite scroll-up pagination.
    /// </summary>
    [HttpGet("projects/{projectName}/logs/history")]
    [ProducesResponseType(typeof(ApiResponse<LogPageDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<LogPageDto>>> GetProjectLogHistory(
        string projectName,
        [FromQuery] int tail = 100,
        [FromQuery] string? until = null,
        [FromQuery] string? services = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);
            tail = Math.Clamp(tail, 1, 1000);

            var (userId, error) = await AuthorizeProjectLogsAsync<LogPageDto>(projectName);
            if (error != null) return error;

            HashSet<string>? serviceFilter = ParseServiceFilter(services);

            IReadOnlyList<string> containerIds =
                await _containerLogService.ListProjectContainerIdsAsync(projectName, includeStopped: true, cancellationToken);

            LogPageDto?[] pages = await Task.WhenAll(containerIds.Select(async id =>
            {
                ContainerLogSource? source = await _containerLogService.GetLogSourceAsync(id, cancellationToken);
                if (source == null)
                {
                    return null;
                }
                if (serviceFilter is { Count: > 0 } && (source.Service == null || !serviceFilter.Contains(source.Service)))
                {
                    return null;
                }
                try
                {
                    return await _containerLogService.GetHistoryAsync(source, tail, until, cancellationToken);
                }
                catch (DockerContainerNotFoundException)
                {
                    // Container removed between listing and fetch — skip it.
                    return null;
                }
            }));

            LogPageDto merged = LogMerger.MergeTail(pages.Where(p => p != null).Cast<LogPageDto>().ToList(), tail);

            return Ok(ApiResponse.Ok(merged, $"Retrieved {merged.Entries.Count} log lines"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse.Fail<LogPageDto>("Invalid 'until' timestamp", "VALIDATION_ERROR"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log history for project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<LogPageDto>("Failed to retrieve project logs", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Streams the unified, real-time logs of every container in the project via SSE.
    /// Events: connected, logs (batched JSON), containers (roster on attach/detach),
    /// error. Containers starting/stopping mid-stream attach/detach automatically.
    /// </summary>
    [HttpGet("projects/{projectName}/logs/stream")]
    public async Task StreamProjectLogs(
        string projectName,
        [FromQuery] int tail = 150,
        [FromQuery] string? since = null,
        [FromQuery] string? services = null,
        CancellationToken cancellationToken = default)
    {
        projectName = Uri.UnescapeDataString(projectName);
        tail = Math.Clamp(tail, 1, 1000);

        if (await _selfFilterService.IsSelfProjectAsync(projectName))
        {
            Response.StatusCode = 403;
            await Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                "This project belongs to the application itself and cannot be accessed",
                "SELF_PROJECT_PROTECTED"), cancellationToken);
            return;
        }

        int? userId = GetCurrentUserId();
        if (!userId.HasValue)
        {
            Response.StatusCode = 401;
            await Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                "User not authenticated", "UNAUTHORIZED"), cancellationToken);
            return;
        }

        bool hasPermission = await _permissionService.HasPermissionAsync(
            userId.Value, ResourceType.ComposeProject, projectName, PermissionFlags.Logs);
        if (!hasPermission)
        {
            Response.StatusCode = 403;
            await Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                "You don't have permission to view logs for this compose project",
                "PERMISSION_DENIED"), cancellationToken);
            return;
        }

        if (since != null)
        {
            try
            {
                LogTimestampUtil.ToUnixNano(since);
            }
            catch (FormatException)
            {
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(ApiResponse.Fail<object>(
                    "Invalid 'since' timestamp", "VALIDATION_ERROR"), cancellationToken);
                return;
            }
        }

        HashSet<string>? serviceFilter = ParseServiceFilter(services);

        await SseLogStreamWriter.RunAsync(
            HttpContext,
            _logStreamCoordinator.StreamProjectAsync(projectName, tail, since, serviceFilter, cancellationToken),
            _logger,
            cancellationToken);
    }

    /// <summary>
    /// Records an operation for a completed project action (create + running + logs +
    /// final status) and builds the HTTP response. Shared by all lifecycle endpoints so
    /// the tracking/response shape stays identical.
    /// </summary>
    private async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> FinalizeOperationAsync(
        string operationType, int userId, string projectName, string? projectPath,
        OperationResult result, string? logs)
    {
        Operation operation = await _legacyOperationService.CreateOperationAsync(
            operationType, userId, projectPath: projectPath, projectName: projectName);
        await _legacyOperationService.UpdateOperationStatusAsync(operation.OperationId, OperationStatus.Running);

        if (!string.IsNullOrEmpty(logs))
            await _legacyOperationService.AppendLogsAsync(operation.OperationId, logs);

        await _legacyOperationService.UpdateOperationStatusAsync(
            operation.OperationId,
            result.Success ? OperationStatus.Completed : OperationStatus.Failed,
            progress: 100,
            errorMessage: result.Success ? null : result.Error);

        if (result.Success)
            _logger.LogInformation("Operation {OperationType} on project {ProjectName} succeeded (user {UserId})",
                operationType, projectName, userId);
        else
            _logger.LogWarning("Operation {OperationType} on project {ProjectName} failed: {Error}",
                operationType, projectName, result.Error);

        ComposeOperationResponse response = new(
            operation.OperationId,
            result.Success ? OperationStatus.Completed : OperationStatus.Failed,
            result.Message);

        return result.Success
            ? Ok(ApiResponse.Ok(response))
            : BadRequest(ApiResponse.Fail<ComposeOperationResponse>(result.Message, "OPERATION_FAILED"));
    }

    #region Helper Methods

    /// <summary>
    /// Start compose services (docker compose start)
    /// </summary>
    [HttpPost("projects/{projectName}/start")]
    public async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> StartProject(string projectName)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            var (userId, error) = await AuthorizeProjectOperationAsync(projectName, PermissionFlags.Start, "start");
            if (error != null) return error;

            string? projectPath = await ResolveProjectPathAsync(projectName, userId);
            OperationResult result = await _operationService.StartAsync(projectName);

            return await FinalizeOperationAsync(
                OperationType.ComposeStart, userId, projectName, projectPath,
                result, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeOperationResponse>("Error starting project", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Stop compose services (docker compose stop)
    /// </summary>
    [HttpPost("projects/{projectName}/stop")]
    public async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> StopProject(string projectName)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            var (userId, error) = await AuthorizeProjectOperationAsync(projectName, PermissionFlags.Stop, "stop");
            if (error != null) return error;

            string? projectPath = await ResolveProjectPathAsync(projectName, userId);
            OperationResult result = await _operationService.StopAsync(projectName);

            return await FinalizeOperationAsync(
                OperationType.ComposeStop, userId, projectName, projectPath,
                result, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeOperationResponse>("Error stopping project", "SERVER_ERROR"));
        }
    }

    /// <summary>
    /// Restart compose services (docker compose restart)
    /// </summary>
    [HttpPost("projects/{projectName}/restart")]
    public async Task<ActionResult<ApiResponse<ComposeOperationResponse>>> RestartProject(string projectName)
    {
        try
        {
            projectName = Uri.UnescapeDataString(projectName);

            var (userId, error) = await AuthorizeProjectOperationAsync(projectName, PermissionFlags.Restart, "restart");
            if (error != null) return error;

            string? projectPath = await ResolveProjectPathAsync(projectName, userId);
            OperationResult result = await _operationService.RestartAsync(projectName);

            return await FinalizeOperationAsync(
                OperationType.ComposeRestart, userId, projectName, projectPath,
                result, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting project: {ProjectName}", projectName);
            return StatusCode(500, ApiResponse.Fail<ComposeOperationResponse>("Error restarting project", "SERVER_ERROR"));
        }
    }

    #endregion

}

public record ComposeTemplateDto(string Id, string Name, string Description, string Content);
