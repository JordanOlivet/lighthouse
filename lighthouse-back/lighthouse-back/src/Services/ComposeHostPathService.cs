using Docker.DotNet.Models;
using Lighthouse.Configuration;
using Lighthouse.Utils;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services;

/// <summary>
/// Makes compose commands run from the host path of the compose directory, so relative
/// paths in compose files resolve exactly as with the host CLI (issue #219).
/// </summary>
public interface IComposeHostPathService
{
    /// <summary>
    /// Returns the path to pass to <c>docker compose -f</c> for <paramref name="composeFilePath"/>:
    /// its host path (reachable inside the container through a symlink) when the compose directory
    /// is mounted at a different path than on the host, otherwise the path unchanged.
    /// </summary>
    Task<string> ToExecutionPathAsync(string composeFilePath, CancellationToken ct = default);

    /// <summary>
    /// Returns an error message when the host path could not be resolved and running the compose
    /// file from Lighthouse would remount the project's relative bind mounts on empty host folders,
    /// or null when the operation is safe.
    /// </summary>
    Task<string?> GetRelativeMountConflictAsync(string projectName, string composeFilePath, CancellationToken ct = default);
}

public class ComposeHostPathService : IComposeHostPathService
{
    private enum HostPathState
    {
        /// <summary>Compose paths are the same as on the host (not in Docker, or same-path mount).</summary>
        Identical,
        /// <summary>The host path is reachable through a symlink; commands use it.</summary>
        Mapped,
        /// <summary>The host path could not be resolved or linked; relative bind mounts are unsafe.</summary>
        Unknown
    }

    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    private const string ProjectLabel = "com.docker.compose.project";

    private readonly ComposeDiscoveryOptions _options;
    private readonly IComposeFileDetectorService _detectorService;
    private readonly IDockerImageOperations _dockerOps;
    private readonly ILogger<ComposeHostPathService> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private HostPathState _state = HostPathState.Identical;
    private string? _hostRoot;

    public ComposeHostPathService(
        IOptions<ComposeDiscoveryOptions> options,
        IComposeFileDetectorService detectorService,
        IDockerImageOperations dockerOps,
        ILogger<ComposeHostPathService> logger)
    {
        _options = options.Value;
        _detectorService = detectorService;
        _dockerOps = dockerOps;
        _logger = logger;
    }

    public async Task<string> ToExecutionPathAsync(string composeFilePath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (_state != HostPathState.Mapped || _hostRoot == null)
            return composeFilePath;

        return ComposeHostPathHelper.ToHostPath(composeFilePath, _options.RootPath, _hostRoot);
    }

    public async Task<string?> GetRelativeMountConflictAsync(string projectName, string composeFilePath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (_state != HostPathState.Unknown)
            return null;

        try
        {
            string content = await File.ReadAllTextAsync(composeFilePath, ct);
            IList<ContainerListResponse> containers = await _dockerOps.ListContainersRawAsync(ct);

            IEnumerable<string?> workingDirs = containers
                .Where(c => c.Labels != null
                    && c.Labels.TryGetValue(ProjectLabel, out string? project)
                    && project.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Labels.TryGetValue(WorkingDirLabel, out string? wd) ? wd : null);

            return ComposeHostPathHelper.GetRelativeMountConflict(
                projectName,
                Path.GetDirectoryName(composeFilePath) ?? "/",
                content,
                workingDirs);
        }
        catch (Exception ex)
        {
            // The guard is best-effort: never block an operation because the check itself failed.
            _logger.LogWarning(ex, "Failed to check relative bind mounts for project {ProjectName}", projectName);
            return null;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            (_state, _hostRoot) = await ResolveStateAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<(HostPathState State, string? HostRoot)> ResolveStateAsync(CancellationToken ct)
    {
        string rootPath = _options.RootPath;

        try
        {
            ComposeDetectionResult detection = await _detectorService.GetComposeDetectionResultAsync();

            // Running directly on the host (dev): compose files are at their real host path.
            if (!detection.IsRunningInDocker || string.IsNullOrEmpty(detection.ContainerId) || !OperatingSystem.IsLinux())
                return (HostPathState.Identical, null);

            string selfId = detection.ContainerId;
            IList<ContainerListResponse> containers = await _dockerOps.ListContainersRawAsync(ct);
            ContainerListResponse? self = containers.FirstOrDefault(c =>
                c.ID.StartsWith(selfId, StringComparison.OrdinalIgnoreCase)
                || selfId.StartsWith(c.ID, StringComparison.OrdinalIgnoreCase));

            IEnumerable<(string?, string?)> bindMounts = (self?.Mounts ?? new List<MountPoint>())
                .Where(m => m.Type == "bind")
                .Select(m => ((string?)m.Source, (string?)m.Destination));

            string? hostRoot = ComposeHostPathHelper.ResolveHostRoot(bindMounts, rootPath);

            // Fall back to the configured mapping when the mounts did not reveal a POSIX host path.
            if (hostRoot == null && _options.HostPathMapping?.StartsWith('/') == true)
                hostRoot = _options.HostPathMapping.TrimEnd('/');

            if (hostRoot == null)
            {
                _logger.LogWarning(
                    "Could not determine the host path of {RootPath}; relative bind mounts in compose files " +
                    "may resolve to the wrong host folders. Mount the compose directory at the same path, or set " +
                    "ComposeDiscovery:HostPathMapping.", rootPath);
                return (HostPathState.Unknown, null);
            }

            if (ComposeHostPathHelper.PathsOverlap(hostRoot, rootPath))
            {
                if (hostRoot.TrimEnd('/') == rootPath.TrimEnd('/'))
                    return (HostPathState.Identical, null);

                _logger.LogWarning(
                    "Host path {HostRoot} overlaps {RootPath}; cannot link it, relative bind mounts may resolve " +
                    "to the wrong host folders.", hostRoot, rootPath);
                return (HostPathState.Unknown, null);
            }

            if (!TryLinkHostRoot(hostRoot, rootPath))
                return (HostPathState.Unknown, null);

            _logger.LogInformation(
                "Compose commands run from host path {HostRoot} (linked to {RootPath}) so relative paths resolve as on the host",
                hostRoot, rootPath);
            return (HostPathState.Mapped, hostRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve the host path of {RootPath}", rootPath);
            return (HostPathState.Unknown, null);
        }
    }

    /// <summary>
    /// Creates <paramref name="hostRoot"/> inside the container as a symlink to
    /// <paramref name="rootPath"/>. Compose keeps the path given to <c>-f</c> as-is (it does not
    /// resolve symlinks), so it computes host paths while still reading the files.
    /// </summary>
    private bool TryLinkHostRoot(string hostRoot, string rootPath)
    {
        try
        {
            if (Directory.Exists(hostRoot) || File.Exists(hostRoot))
            {
                string? target = new DirectoryInfo(hostRoot).LinkTarget;
                if (target != null && target.TrimEnd('/') == rootPath.TrimEnd('/'))
                    return true;

                _logger.LogWarning(
                    "Cannot link host path {HostRoot} to {RootPath}: the path already exists in the container; " +
                    "relative bind mounts may resolve to the wrong host folders.", hostRoot, rootPath);
                return false;
            }

            string? parent = Path.GetDirectoryName(hostRoot);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            Directory.CreateSymbolicLink(hostRoot, rootPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to link host path {HostRoot} to {RootPath}", hostRoot, rootPath);
            return false;
        }
    }
}
