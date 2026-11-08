namespace Lighthouse.Services;

using Lighthouse.Models;

/// <summary>
/// Cached access to compose file discovery results (stale-while-revalidate).
/// </summary>
/// <remarks>
/// Once a snapshot exists, reads never wait on the filesystem: a stale snapshot is served
/// immediately while a single background rescan refreshes it. Only a read with no snapshot
/// (startup, or after <see cref="Invalidate"/>) awaits a scan. See <see cref="ComposeFileCacheService"/>.
/// </remarks>
public interface IComposeFileCacheService
{
    /// <summary>
    /// Gets discovered compose files. Returns the cached snapshot (even if stale, in which case a
    /// background rescan is triggered), or awaits a scan when there is no snapshot.
    /// </summary>
    /// <param name="bypassCache">If true, invalidates the cache and awaits a fresh scan.</param>
    /// <returns>List of discovered compose files with metadata.</returns>
    /// <remarks>Thread-safe: concurrent callers share a single in-flight scan.</remarks>
    Task<List<DiscoveredComposeFile>> GetOrScanAsync(bool bypassCache = false);

    /// <summary>
    /// Gets discovered compose files no older than the configured TTL: returns a fresh snapshot,
    /// otherwise awaits a rescan (sharing any in-flight scan). The current snapshot stays served
    /// to other callers meanwhile.
    /// </summary>
    /// <remarks>
    /// For background jobs that act on file contents (e.g. auto-update honouring
    /// <c>x-auto-update</c>) and must not rely on a stale snapshot. Not for user-facing reads.
    /// </remarks>
    Task<List<DiscoveredComposeFile>> GetFreshAsync();

    /// <summary>
    /// Drops the cached snapshot so the next <see cref="GetOrScanAsync"/> awaits a fresh scan.
    /// </summary>
    /// <remarks>
    /// Call after compose files are changed by the application (editor save) or when the user
    /// explicitly asks for a refresh, so the next read reflects the change.
    /// </remarks>
    void Invalidate();
}
