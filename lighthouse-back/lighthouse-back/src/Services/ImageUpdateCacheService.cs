using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services;

/// <summary>
/// Service for caching project update check results.
/// </summary>
public interface IImageUpdateCacheService
{
    /// <summary>
    /// Gets cached update check result for a project.
    /// </summary>
    ProjectUpdateCheckResponse? GetCachedCheck(string projectName);

    /// <summary>
    /// Sets cached update check result for a project.
    /// </summary>
    void SetCachedCheck(string projectName, ProjectUpdateCheckResponse result);

    /// <summary>
    /// Invalidates cache for a specific project.
    /// </summary>
    void InvalidateProject(string projectName);

    /// <summary>
    /// Invalidates all cached update checks.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Gets all cached project summaries.
    /// </summary>
    List<ProjectUpdateSummary> GetAllCachedSummaries();
}

public class ImageUpdateCacheService : IImageUpdateCacheService
{
    private readonly IMemoryCache _cache;
    private readonly UpdateCheckOptions _options;
    private readonly UpdateCheckIntervalState _intervalState;
    private readonly ILogger<ImageUpdateCacheService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Track cached project names for bulk operations
    private readonly HashSet<string> _cachedProjects = new();
    private readonly object _projectsLock = new();

    private const string CacheKeyPrefix = "image_update_";

    public ImageUpdateCacheService(
        IMemoryCache cache,
        UpdateCheckIntervalState intervalState,
        IOptions<UpdateCheckOptions> options,
        ILogger<ImageUpdateCacheService> logger)
    {
        _cache = cache;
        _intervalState = intervalState;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Cache lifetime for a project's check result. Tracks the effective check interval (so cached
    /// status survives until the next check, whatever interval the user picks) plus 0–20% random
    /// jitter so the projects cached in one cycle don't all expire on the same minute.
    /// </summary>
    private TimeSpan GetCacheLifetime()
    {
        int intervalMinutes = _intervalState.IntervalMinutes;
        if (intervalMinutes <= 0)
        {
            intervalMinutes = _options.CacheDurationMinutes;
        }

        double jitterFactor = 1.0 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromMinutes(intervalMinutes * jitterFactor);
    }

    public ProjectUpdateCheckResponse? GetCachedCheck(string projectName)
    {
        string cacheKey = GetCacheKey(projectName);

        if (_cache.TryGetValue(cacheKey, out ProjectUpdateCheckResponse? cached))
        {
            _logger.LogDebug("Cache hit for project {ProjectName}", projectName);
            return cached;
        }

        _logger.LogDebug("Cache miss for project {ProjectName}", projectName);
        return null;
    }

    public void SetCachedCheck(string projectName, ProjectUpdateCheckResponse result)
    {
        string cacheKey = GetCacheKey(projectName);

        TimeSpan lifetime = GetCacheLifetime();
        var cacheOptions = new MemoryCacheEntryOptions
        {
            // No sliding expiration: the entry should live ~one check interval and then be refreshed
            // by the next cycle, not be kept alive indefinitely by dashboard reads.
            AbsoluteExpirationRelativeToNow = lifetime
        };

        // Set up removal callback to clean up tracking
        cacheOptions.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            if (reason != EvictionReason.Replaced)
            {
                lock (_projectsLock)
                {
                    _cachedProjects.Remove(projectName);
                }
            }
        });

        _cache.Set(cacheKey, result, cacheOptions);

        lock (_projectsLock)
        {
            _cachedProjects.Add(projectName);
        }

        _logger.LogDebug("Cached update check for project {ProjectName}, expires in {Minutes:0.0} minutes",
            projectName, lifetime.TotalMinutes);
    }

    public void InvalidateProject(string projectName)
    {
        string cacheKey = GetCacheKey(projectName);
        _cache.Remove(cacheKey);

        lock (_projectsLock)
        {
            _cachedProjects.Remove(projectName);
        }

        _logger.LogDebug("Invalidated cache for project {ProjectName}", projectName);
    }

    public void InvalidateAll()
    {
        List<string> projects;
        lock (_projectsLock)
        {
            projects = _cachedProjects.ToList();
            _cachedProjects.Clear();
        }

        foreach (string projectName in projects)
        {
            string cacheKey = GetCacheKey(projectName);
            _cache.Remove(cacheKey);
        }

        _logger.LogDebug("Invalidated all cached update checks ({Count} projects)", projects.Count);
    }

    public List<ProjectUpdateSummary> GetAllCachedSummaries()
    {
        var summaries = new List<ProjectUpdateSummary>();

        List<string> projects;
        lock (_projectsLock)
        {
            projects = _cachedProjects.ToList();
        }

        foreach (string projectName in projects)
        {
            ProjectUpdateCheckResponse? cached = GetCachedCheck(projectName);
            if (cached != null)
            {
                summaries.Add(new ProjectUpdateSummary(
                    ProjectName: cached.ProjectName,
                    ServicesWithUpdates: Lighthouse.Utils.UpdateCountHelper.CountServicesWithUpdates(cached.Images, excludeDisabled: false),
                    LastChecked: cached.LastChecked,
                    HasRunningServices: cached.Images.Any(i =>
                        string.Equals(i.ContainerState, "running", StringComparison.OrdinalIgnoreCase))
                ));
            }
        }

        return summaries;
    }

    private static string GetCacheKey(string projectName)
    {
        return $"{CacheKeyPrefix}{projectName.ToLowerInvariant()}";
    }
}
