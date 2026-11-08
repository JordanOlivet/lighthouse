using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Utils;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace Lighthouse.Services;

/// <summary>
/// Read-only half of the compose update feature: resolves a project's compose file, parses its
/// service images and queries the registries (via <see cref="IImageDigestService"/>) for available
/// updates, caching the results. Split out of <see cref="ComposeUpdateService"/> so the
/// check/lookup concern is isolated from the update orchestration.
/// </summary>
public interface IComposeUpdateChecker
{
    /// <summary>Checks for available updates for a project's images (cached, with container states).</summary>
    Task<ProjectUpdateCheckResponse> CheckProjectUpdatesAsync(
        string projectName,
        bool forceRefresh = false,
        CancellationToken ct = default);

    /// <summary>Checks for updates across all projects that have compose files.</summary>
    Task<CheckAllUpdatesResponse> CheckAllProjectsUpdatesAsync(
        int userId,
        bool forceRefresh = false,
        CancellationToken ct = default);

    /// <summary>Gets the global update status for all cached projects.</summary>
    List<ProjectUpdateSummary> GetGlobalUpdateStatus();

    /// <summary>Clears the update check cache.</summary>
    void ClearCache();

    /// <summary>Resolves the compose file path for a project (used by the update orchestration too).</summary>
    Task<string?> FindComposeFilePathAsync(string projectName);
}

public class ComposeUpdateChecker : IComposeUpdateChecker
{
    private readonly IImageDigestService _imageDigestService;
    private readonly IImageUpdateCacheService _cacheService;
    private readonly IComposeFileCacheService _fileCacheService;
    private readonly IProjectMatchingService _projectMatchingService;
    private readonly DockerService _dockerService;
    private readonly SseConnectionManagerService _sseManager;
    private readonly IComposeEnvFileResolver _envFileResolver;
    private readonly UpdateCheckOptions _options;
    private readonly ILogger<ComposeUpdateChecker> _logger;

    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public ComposeUpdateChecker(
        IImageDigestService imageDigestService,
        IImageUpdateCacheService cacheService,
        IComposeFileCacheService fileCacheService,
        IProjectMatchingService projectMatchingService,
        DockerService dockerService,
        SseConnectionManagerService sseManager,
        IComposeEnvFileResolver envFileResolver,
        IOptions<UpdateCheckOptions> options,
        ILogger<ComposeUpdateChecker> logger)
    {
        _imageDigestService = imageDigestService;
        _cacheService = cacheService;
        _fileCacheService = fileCacheService;
        _projectMatchingService = projectMatchingService;
        _dockerService = dockerService;
        _sseManager = sseManager;
        _envFileResolver = envFileResolver;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProjectUpdateCheckResponse> CheckProjectUpdatesAsync(
        string projectName,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (forceRefresh)
            _cacheService.InvalidateProject(projectName);

        ProjectUpdateCheckResponse result = await CheckProjectUpdatesInternalAsync(projectName, null, ct);

        // Enrich images with container state from Docker
        return await EnrichWithContainerStatesAsync(projectName, result);
    }

    private async Task<ProjectUpdateCheckResponse> CheckProjectUpdatesInternalAsync(
        string projectName,
        string? knownComposeFilePath,
        CancellationToken ct = default)
    {
        // Check cache first
        ProjectUpdateCheckResponse? cached = _cacheService.GetCachedCheck(projectName);
        if (cached != null)
        {
            return cached;
        }

        await _checkLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            cached = _cacheService.GetCachedCheck(projectName);
            if (cached != null)
            {
                return cached;
            }

            _logger.LogDebug("Checking updates for project {ProjectName}", projectName);

            // Use pre-resolved path if available, otherwise look it up
            string? composeFilePath = knownComposeFilePath ?? await FindComposeFilePathAsync(projectName);

            if (composeFilePath == null)
            {
                _logger.LogWarning("No compose file found for project {ProjectName}", projectName);
                return new ProjectUpdateCheckResponse(
                    ProjectName: projectName,
                    Images: new List<ImageUpdateStatus>(),
                    HasUpdates: false,
                    LastChecked: DateTime.UtcNow
                );
            }

            _logger.LogDebug("Found compose file for project {ProjectName}: {FilePath}", projectName, composeFilePath);

            // Parse compose file to get services and images
            Dictionary<string, ServiceImageInfo> serviceImages = await ParseServiceImagesAsync(composeFilePath, ct);

            // Check each image for updates (with concurrency limit)
            SemaphoreSlim semaphore = new SemaphoreSlim(_options.MaxConcurrentChecks);
            List<Task<ImageUpdateStatus>> tasks = new List<Task<ImageUpdateStatus>>();

            foreach ((string? serviceName, ServiceImageInfo? imageInfo) in serviceImages)
            {
                if (string.IsNullOrEmpty(imageInfo.Image))
                {
                    // Service uses build, not image
                    continue;
                }

                // Check if image is excluded
                if (IsImageExcluded(imageInfo.Image))
                {
                    _logger.LogDebug("Image {Image} is excluded from update checks", imageInfo.Image);
                    continue;
                }

                tasks.Add(CheckImageWithSemaphoreAsync(
                    semaphore,
                    imageInfo.Image,
                    serviceName,
                    imageInfo.UpdatePolicy,
                    ct));
            }

            ImageUpdateStatus[] results = await Task.WhenAll(tasks);

            ProjectUpdateCheckResponse response = new ProjectUpdateCheckResponse(
                ProjectName: projectName,
                Images: results.ToList(),
                HasUpdates: results.Any(r => r.UpdateAvailable && r.UpdatePolicy != "disabled"),
                LastChecked: DateTime.UtcNow
            );

            // Cache the result
            _cacheService.SetCachedCheck(projectName, response);

            _logger.LogDebug(
                "Update check complete for {ProjectName}: {UpdateCount} updates available out of {TotalCount} images",
                projectName,
                results.Count(r => r.UpdateAvailable),
                results.Length);

            return response;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<ImageUpdateStatus> CheckImageWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        string image,
        string serviceName,
        string? updatePolicy,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            ImageUpdateStatus status = await _imageDigestService.CheckImageUpdateAsync(image, serviceName, ct);

            // Apply update policy from compose file
            return status with { UpdatePolicy = updatePolicy };
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<ProjectUpdateCheckResponse> EnrichWithContainerStatesAsync(
        string projectName,
        ProjectUpdateCheckResponse response)
    {
        try
        {
            List<ContainerDto> allContainers = await _dockerService.ListContainersAsync();
            Dictionary<string, string> serviceStateMap = allContainers
                .Where(c => c.Labels != null
                    && string.Equals(
                        c.Labels.GetValueOrDefault("com.docker.compose.project"),
                        projectName,
                        StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Labels!.GetValueOrDefault("com.docker.compose.service") ?? "")
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(
                    g => g.Key,
                    g => g.First().State,
                    StringComparer.OrdinalIgnoreCase);

            List<ImageUpdateStatus> enrichedImages = response.Images
                .Select(img => img with
                {
                    ContainerState = serviceStateMap.GetValueOrDefault(img.ServiceName)
                })
                .ToList();

            return response with { Images = enrichedImages };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich container states for project {ProjectName}", projectName);
            return response;
        }
    }

    public List<ProjectUpdateSummary> GetGlobalUpdateStatus()
    {
        return _cacheService.GetAllCachedSummaries();
    }

    public void ClearCache()
    {
        _cacheService.InvalidateAll();
        _logger.LogDebug("Update check cache cleared");
    }

    public async Task<CheckAllUpdatesResponse> CheckAllProjectsUpdatesAsync(
        int userId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Checking updates for all projects (user: {UserId}, forceRefresh: {ForceRefresh})", userId, forceRefresh);

        if (forceRefresh)
            _cacheService.InvalidateAll();

        // Get all projects with compose files
        List<ComposeProjectDto> allProjects = await _projectMatchingService.GetUnifiedProjectListAsync(userId);
        List<ComposeProjectDto> projectsWithFiles = allProjects
            .Where(p => p.HasComposeFile && !string.IsNullOrEmpty(p.ComposeFilePath))
            .ToList();

        _logger.LogDebug("Found {Count} projects with compose files to check", projectsWithFiles.Count);

        List<ProjectUpdateSummary> summaries = new List<ProjectUpdateSummary>();
        int totalServicesWithUpdates = 0;

        // Check each project sequentially to avoid rate limiting
        foreach (ComposeProjectDto? project in projectsWithFiles)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                ProjectUpdateCheckResponse checkResult = await CheckProjectUpdatesInternalAsync(project.Name, project.ComposeFilePath, ct);

                int servicesWithUpdates = checkResult.Images
                    .Count(i => i.UpdateAvailable && i.UpdatePolicy != "disabled");

                bool hasRunningServices = project.Services.Any(s =>
                    string.Equals(s.State, "running", StringComparison.OrdinalIgnoreCase));

                summaries.Add(new ProjectUpdateSummary(
                    ProjectName: project.Name,
                    ServicesWithUpdates: servicesWithUpdates,
                    LastChecked: checkResult.LastChecked,
                    HasRunningServices: hasRunningServices
                ));

                totalServicesWithUpdates += servicesWithUpdates;

                _logger.LogDebug(
                    "Project {ProjectName}: {UpdateCount} services with updates",
                    project.Name,
                    servicesWithUpdates);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking updates for project {ProjectName}", project.Name);
                // Add project with 0 updates on error so it still appears in the list
                summaries.Add(new ProjectUpdateSummary(
                    ProjectName: project.Name,
                    ServicesWithUpdates: 0,
                    LastChecked: null
                ));
            }
        }

        int projectsWithUpdates = summaries.Count(s => s.ServicesWithUpdates > 0);

        _logger.LogDebug(
            "Bulk update check complete: {ProjectsChecked} projects checked, {ProjectsWithUpdates} with updates, {TotalServices} total services",
            summaries.Count,
            projectsWithUpdates,
            totalServicesWithUpdates);

        CheckAllUpdatesResponse response = new CheckAllUpdatesResponse(
            Projects: summaries,
            ProjectsChecked: summaries.Count,
            ProjectsWithUpdates: projectsWithUpdates,
            TotalServicesWithUpdates: totalServicesWithUpdates,
            CheckedAt: DateTime.UtcNow
        );

        // Broadcast SSE event for manual check
        try
        {
            ProjectUpdatesCheckedEvent sseEvent = new(
                Projects: response.Projects,
                ProjectsChecked: response.ProjectsChecked,
                ProjectsWithUpdates: response.ProjectsWithUpdates,
                TotalServicesWithUpdates: response.TotalServicesWithUpdates,
                CheckedAt: response.CheckedAt,
                Trigger: "manual"
            );
            await _sseManager.BroadcastAsync("ProjectUpdatesChecked", sseEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast ProjectUpdatesChecked SSE event");
        }

        return response;
    }

    private async Task<Dictionary<string, ServiceImageInfo>> ParseServiceImagesAsync(
        string composeFilePath,
        CancellationToken ct)
    {
        Dictionary<string, ServiceImageInfo> result = new Dictionary<string, ServiceImageInfo>();

        try
        {
            string content = await File.ReadAllTextAsync(composeFilePath, ct);

            IDeserializer deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();

            Dictionary<string, object> composeData = deserializer.Deserialize<Dictionary<string, object>>(content);

            // Built lazily: only compose files with variables in an image need the env files.
            Func<string, string?>? variableLookup = null;

            if (composeData == null ||
                !composeData.TryGetValue("services", out object? servicesObj) ||
                servicesObj is not Dictionary<object, object> services)
            {
                return result;
            }

            foreach ((object? serviceKey, object? serviceValue) in services)
            {
                string serviceName = serviceKey.ToString() ?? "";
                if (serviceValue is not Dictionary<object, object> serviceData)
                    continue;

                string? image = null;
                string? updatePolicy = null;

                if (serviceData.TryGetValue("image", out object? imageObj))
                {
                    image = imageObj?.ToString();
                }

                if (image != null && image.Contains('$'))
                {
                    variableLookup ??= await BuildVariableLookupAsync(composeFilePath, ct);
                    string resolved = ComposeVariableInterpolator.Interpolate(image, variableLookup, out IReadOnlyList<string> unresolved);
                    if (unresolved.Count > 0)
                    {
                        // Compose would substitute "" and fail on the resulting invalid reference too.
                        _logger.LogWarning(
                            "Image {Image} of service {Service} in {Path} uses unset variable(s) {Variables}; skipping its update check",
                            image, serviceName, composeFilePath, string.Join(", ", unresolved));
                        image = null;
                    }
                    else
                    {
                        image = resolved;
                    }
                }

                // Check for x-update-policy extension
                if (serviceData.TryGetValue("x-update-policy", out object? policyObj))
                {
                    updatePolicy = policyObj?.ToString()?.ToLowerInvariant();
                }

                // Also check at root level for project-wide policy
                if (updatePolicy == null && composeData.TryGetValue("x-update-policy", out object? rootPolicyObj))
                {
                    updatePolicy = rootPolicyObj?.ToString()?.ToLowerInvariant();
                }

                result[serviceName] = new ServiceImageInfo(image, updatePolicy);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing compose file {Path}", composeFilePath);
        }

        return result;
    }

    /// <summary>
    /// Builds the variable lookup docker compose would use to interpolate the file: values from the
    /// env files (global, then project <c>.env</c>), overridden by the process environment.
    /// </summary>
    private async Task<Func<string, string?>> BuildVariableLookupAsync(string composeFilePath, CancellationToken ct)
    {
        var fileVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            IReadOnlyList<string> envFiles = await _envFileResolver.GetEnvFilePathsAsync(Path.GetDirectoryName(composeFilePath), ct);
            foreach (string envFile in envFiles)
            {
                foreach ((string key, string value) in ComposeVariableInterpolator.ParseDotEnv(await File.ReadAllTextAsync(envFile, ct)))
                {
                    fileVariables[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read env files for {Path}; interpolating with the process environment only", composeFilePath);
        }

        return name => Environment.GetEnvironmentVariable(name)
            ?? (fileVariables.TryGetValue(name, out string? value) ? value : null);
    }

    private bool IsImageExcluded(string image)
    {
        foreach (string pattern in _options.ExcludedImages)
        {
            if (MatchesPattern(image, pattern))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        // Simple wildcard matching
        if (pattern.Contains('*'))
        {
            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                value,
                regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the compose file path for a project using the ProjectMatchingService.
    /// This reuses the same sophisticated matching logic used for the projects list,
    /// handling cases where Docker's project name differs from the compose file's project name.
    /// </summary>
    /// <param name="projectName">The project name as reported by Docker</param>
    /// <returns>The compose file path if found, null otherwise</returns>
    public async Task<string?> FindComposeFilePathAsync(string projectName)
    {
        try
        {
            // Fast path: most projects map to a discovered file whose ProjectName matches directly
            // (this is the dominant "Match found by project name" case). Resolving it from the cached
            // discovery avoids rebuilding the whole unified project list (docker compose ls + matching
            // + per-project cache scan) just to find one project's compose file.
            List<Models.DiscoveredComposeFile> discoveredFiles = await _fileCacheService.GetOrScanAsync();
            Models.DiscoveredComposeFile? directMatch = discoveredFiles.FirstOrDefault(f =>
                f.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (directMatch != null && !string.IsNullOrEmpty(directMatch.FilePath))
            {
                _logger.LogDebug(
                    "Found compose file by name for project {ProjectName}: {FilePath}",
                    projectName, directMatch.FilePath);
                return directMatch.FilePath;
            }

            // Fallback: the full unified list handles edge cases the direct lookup misses
            // (path-based / filename+directory matching, Docker project name differing from the file name).
            // We use userId 1 (default admin) since this is only called from admin endpoints
            const int systemAdminUserId = 1;
            List<ComposeProjectDto> projects = await _projectMatchingService.GetUnifiedProjectListAsync(systemAdminUserId);

            // Find the project by name (case-insensitive)
            ComposeProjectDto? matchedProject = projects.FirstOrDefault(p =>
                p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (matchedProject != null && matchedProject.HasComposeFile)
            {
                _logger.LogDebug(
                    "Found compose file via ProjectMatchingService for project {ProjectName}: {FilePath}",
                    projectName,
                    matchedProject.ComposeFilePath);
                return matchedProject.ComposeFilePath;
            }

            _logger.LogDebug(
                "No compose file found via ProjectMatchingService for project {ProjectName}",
                projectName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding compose file path for project {ProjectName}", projectName);
            return null;
        }
    }

    private record ServiceImageInfo(string? Image, string? UpdatePolicy);
}
