namespace Lighthouse.Services;

using Lighthouse.Configuration;
using Lighthouse.Models;
using Microsoft.Extensions.Options;

/// <summary>
/// Singleton, stale-while-revalidate cache for discovered compose files.
/// </summary>
/// <remarks>
/// <para>
/// Compose files usually live on spinning disks that go to standby when idle. A cold
/// filesystem scan can then block for tens of seconds while the disks spin up, whereas a
/// warm scan takes a few milliseconds. To keep user-facing requests fast, this cache never
/// makes a caller wait on the disk once it holds a snapshot:
/// </para>
/// <list type="bullet">
/// <item>Fresh snapshot (younger than <c>CacheDurationSeconds</c>): returned as-is.</item>
/// <item>Stale snapshot: returned immediately, and a single background rescan is started.
/// When that rescan finds a different set of files, a <c>ComposeFilesChanged</c> SSE event
/// is broadcast so clients refetch.</item>
/// <item>No snapshot (startup, or after <see cref="Invalidate"/>): the caller awaits a scan.</item>
/// </list>
/// <para>
/// All concurrent callers share one in-flight scan per cache generation. <see cref="Invalidate"/>
/// bumps the generation, so a scan started before an invalidation (e.g. before a file was
/// saved) is never handed to callers that need post-invalidation data, and its result is
/// discarded instead of overwriting a newer snapshot.
/// </para>
/// </remarks>
public class ComposeFileCacheService : IComposeFileCacheService
{
    /// <summary>SSE event broadcast when a background rescan finds changed compose files.</summary>
    public const string FilesChangedEvent = "ComposeFilesChanged";

    private readonly IComposeFileScanner _scanner;
    private readonly IOptions<ComposeDiscoveryOptions> _options;
    private readonly SseConnectionManagerService _sseManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComposeFileCacheService> _logger;

    private readonly object _sync = new();
    private Snapshot? _snapshot;
    private long _generation;
    private InFlightScan? _inFlight;

    public ComposeFileCacheService(
        IComposeFileScanner scanner,
        IOptions<ComposeDiscoveryOptions> options,
        SseConnectionManagerService sseManager,
        ILogger<ComposeFileCacheService> logger,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sseManager = sseManager ?? throw new ArgumentNullException(nameof(sseManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<List<DiscoveredComposeFile>> GetOrScanAsync(bool bypassCache = false)
    {
        if (bypassCache)
        {
            Invalidate();
        }

        Snapshot? snapshot;
        lock (_sync)
        {
            snapshot = _snapshot;
        }

        if (snapshot != null)
        {
            if (IsStale(snapshot))
            {
                _logger.LogDebug("Compose file cache is stale - serving it and rescanning in background");
                StartBackgroundRefresh();
            }
            else
            {
                _logger.LogDebug("Cache HIT for compose file discovery");
            }

            return snapshot.Files;
        }

        _logger.LogDebug("Cache MISS for compose file discovery - awaiting scan");
        return await GetOrStartScan().Task;
    }

    /// <inheritdoc />
    public async Task<List<DiscoveredComposeFile>> GetFreshAsync()
    {
        Snapshot? snapshot;
        lock (_sync)
        {
            snapshot = _snapshot;
        }

        if (snapshot != null && !IsStale(snapshot))
        {
            return snapshot.Files;
        }

        return await GetOrStartScan().Task;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_sync)
        {
            _snapshot = null;
            _generation++;
        }

        _logger.LogDebug("Compose file discovery cache invalidated");
    }

    private bool IsStale(Snapshot snapshot)
    {
        TimeSpan ttl = TimeSpan.FromSeconds(_options.Value.CacheDurationSeconds);
        return _timeProvider.GetUtcNow() - snapshot.ScannedAt >= ttl;
    }

    private void StartBackgroundRefresh()
    {
        Task<List<DiscoveredComposeFile>> scan = GetOrStartScan().Task;

        // Observe failures so they are logged once and never surface as unobserved exceptions.
        // The stale snapshot is kept on failure; the next stale read retries.
        _ = scan.ContinueWith(
            t => _logger.LogWarning(t.Exception?.GetBaseException(), "Background compose file rescan failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Returns the in-flight scan for the current generation, or starts a new one.
    /// </summary>
    private InFlightScan GetOrStartScan()
    {
        lock (_sync)
        {
            if (_inFlight != null && _inFlight.Generation == _generation && !_inFlight.Task.IsCompleted)
            {
                _logger.LogDebug("Joining in-flight compose file scan");
                return _inFlight;
            }

            long generation = _generation;
            Snapshot? previous = _snapshot;

            // Task.Run: the scanner does synchronous directory enumeration before its first await,
            // which would otherwise block the calling request thread while the disks spin up.
            Task<List<DiscoveredComposeFile>> task = Task.Run(() => ScanAndStoreAsync(generation, previous));
            _inFlight = new InFlightScan(generation, task);
            return _inFlight;
        }
    }

    private async Task<List<DiscoveredComposeFile>> ScanAndStoreAsync(long generation, Snapshot? previous)
    {
        _logger.LogDebug("Starting compose file discovery scan");
        List<DiscoveredComposeFile> files;
        try
        {
            files = await _scanner.ScanComposeFilesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during compose file discovery scan");
            throw;
        }

        bool stored;
        lock (_sync)
        {
            // Drop results of a scan that predates an invalidation: a newer scan owns the cache.
            stored = generation == _generation;
            if (stored)
            {
                _snapshot = new Snapshot(files, _timeProvider.GetUtcNow());
            }
        }

        if (!stored)
        {
            _logger.LogDebug("Discarding compose file scan result from an invalidated generation");
            return files;
        }

        _logger.LogDebug(
            "Cache populated with {Count} compose files, TTL: {TtlSeconds}s",
            files.Count,
            _options.Value.CacheDurationSeconds);

        // Only a background refresh (replacing an existing snapshot) can change what clients
        // already display; a cold scan's result is returned directly to its callers.
        if (previous != null && !HaveSameContent(previous.Files, files))
        {
            _logger.LogInformation("Compose files changed on disk - notifying clients");
            await _sseManager.BroadcastAsync(FilesChangedEvent, new { count = files.Count });
        }

        return files;
    }

    private static bool HaveSameContent(List<DiscoveredComposeFile> a, List<DiscoveredComposeFile> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        HashSet<string> fingerprints = a.Select(Fingerprint).ToHashSet(StringComparer.Ordinal);
        return b.All(f => fingerprints.Contains(Fingerprint(f)));
    }

    private static string Fingerprint(DiscoveredComposeFile f) =>
        string.Join('|',
            f.FilePath,
            f.ProjectName,
            f.LastModified.Ticks,
            f.IsValid,
            f.IsDisabled,
            f.AutoUpdateEnabled,
            string.Join(',', f.Services));

    private sealed record Snapshot(List<DiscoveredComposeFile> Files, DateTimeOffset ScannedAt);

    private sealed record InFlightScan(long Generation, Task<List<DiscoveredComposeFile>> Task);
}
