using Docker.DotNet;
using Docker.DotNet.Models;
using Lighthouse.DTOs;
using Lighthouse.src.Utils;
using Lighthouse.Utils;

namespace Lighthouse.Services;

public class DockerService : IDockerImageOperations
{
    private readonly DockerClient _dockerClient;
    private readonly ILogger<DockerService> _logger;
    private readonly CrashLoopDetectionService _crashLoopDetection;

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger, CrashLoopDetectionService crashLoopDetection)
    {
        _logger = logger;
        _crashLoopDetection = crashLoopDetection;

        string? dockerHost = configuration["Docker:Host"];

        if (string.IsNullOrEmpty(dockerHost))
        {
            throw new ArgumentException("Unable to initialize Docker client with an empty docker host. You have to set it with the env var 'Docker__Host' in the environment section of the compose file.");
        }

        try
        {
            _dockerClient = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
            _logger.LogDebug("Docker client initialized with host: {DockerHost}", dockerHost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Docker client with host: {DockerHost}", dockerHost);
            throw;
        }
    }

    public async Task<List<ContainerDto>> ListContainersAsync(bool showAll = true)
    {
        try
        {
            IList<ContainerListResponse> containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = showAll });

            Dictionary<string, OneShotInfo> oneShots = await ResolveOneShotsRawAsync(containers);

            return containers.Select(c =>
            {
                OneShotInfo? oneShot = oneShots.GetValueOrDefault(c.ID);
                return new ContainerDto(
                    c.ID,
                    NormalizeName(c.Names.FirstOrDefault()),
                    c.Image,
                    c.Status,
                    c.State.ToEntityState().ToStateString(),
                    c.Created,
                    c.Labels != null ? new Dictionary<string, string>(c.Labels) : null,
                    Ports: c.Ports?.Where(p => p.PublicPort > 0).Select(p => $"{p.PublicPort}:{p.PrivatePort}").Distinct().ToList(),
                    IpAddress: c.NetworkSettings?.Networks?.Values.FirstOrDefault()?.IPAddress,
                    IsCrashLooping: _crashLoopDetection.IsContainerCrashLooping(c.ID),
                    IsOneShot: oneShot != null,
                    OneShotStatus: oneShot?.Status.ToString(),
                    ExitCode: oneShot?.ExitCode,
                    FinishedAt: oneShot?.FinishedAt
                );
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing containers");
            throw;
        }
    }

    public async Task<ContainerDetailsDto?> GetContainerDetailsAsync(string containerId)
    {
        try
        {
            ContainerInspectResponse container = await _dockerClient.Containers.InspectContainerAsync(containerId);

            List<MountDto>? mounts = container.Mounts?.Select(m => new MountDto(
                m.Type,
                m.Source,
                m.Destination,
                !m.RW
            )).ToList();

            List<string>? networks = container.NetworkSettings?.Networks?.Keys.ToList();

            Dictionary<string, string>? ports = container.NetworkSettings?.Ports?
                .Where(p => p.Value != null)
                .ToDictionary(
                    p => p.Key,
                    p => string.Join(", ", p.Value.Select(b => $"{b.HostIP}:{b.HostPort}"))
                );

            Dictionary<string, string>? envDict = container.Config?.Env?
                .Select(e => e.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);

            // Extract simplified ports (e.g. "7070:80") from port bindings
            List<string>? simplePorts = container.NetworkSettings?.Ports?
                .Where(p => p.Value != null && p.Value.Any(b => !string.IsNullOrEmpty(b.HostPort)))
                .SelectMany(p =>
                {
                    // p.Key is like "80/tcp", extract the port number
                    string containerPort = p.Key.Split('/')[0];
                    return p.Value
                        .Where(b => !string.IsNullOrEmpty(b.HostPort))
                        .Select(b => $"{b.HostPort}:{containerPort}");
                })
                .Distinct()
                .ToList();

            return new ContainerDetailsDto(
                container.ID,
                NormalizeName(container.Name),
                container.Config?.Image ?? "unknown",
                container.State?.Status ?? "unknown",
                container.State?.Status?.ToEntityState().ToStateString() ?? "Unknown",
                container.Created,
                container.Config?.Labels != null ? new Dictionary<string, string>(container.Config.Labels) : null,
                envDict,
                mounts,
                networks,
                PortDetails: ports,
                IpAddress: container.NetworkSettings?.Networks?.Values.FirstOrDefault()?.IPAddress,
                Ports: simplePorts
            );
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Container {ContainerId} not found", containerId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting container details for {ContainerId}", containerId);
            return null;
        }
    }

    public async Task<bool> StartContainerAsync(string containerId)
    {
        try
        {
            await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            _logger.LogDebug("Container {ContainerId} started", containerId);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot start container {ContainerId}: not found", containerId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<bool> StopContainerAsync(string containerId)
    {
        try
        {
            await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
            _logger.LogDebug("Container {ContainerId} stopped", containerId);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot stop container {ContainerId}: not found", containerId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<bool> RestartContainerAsync(string containerId)
    {
        try
        {
            await _dockerClient.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters());
            _logger.LogDebug("Container {ContainerId} restarted", containerId);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot restart container {ContainerId}: not found", containerId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<bool> RemoveContainerAsync(string containerId, bool force = false)
    {
        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = force });
            _logger.LogDebug("Container {ContainerId} removed", containerId);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot remove container {ContainerId}: not found", containerId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<List<string>> GetContainerLogsAsync(string containerId, int tail = 100, bool timestamps = false)
    {
        try
        {
            ContainerLogsParameters parameters = new()
            {
                ShowStdout = true,
                ShowStderr = true,
                Tail = tail.ToString(),
                Timestamps = timestamps
            };

            MultiplexedStream logs = await _dockerClient.Containers.GetContainerLogsAsync(
                containerId,
                true,
                parameters,
                CancellationToken.None
            );

            List<string> logLines = new();
            (string stdout, string stderr) = await logs.ReadOutputToEndAsync(CancellationToken.None);

            // Combine stdout and stderr
            if (!string.IsNullOrEmpty(stdout))
            {
                logLines.AddRange(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                logLines.AddRange(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }

            _logger.LogDebug("Retrieved {LineCount} log lines from container {ContainerId}", logLines.Count, containerId);
            return logLines;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot get logs for container {ContainerId}: not found", containerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting logs for container {ContainerId}", containerId);
            throw;
        }
    }

    public async Task<ContainerStatsDto?> GetContainerStatsAsync(string containerId)
    {
        try
        {
            // First, check if container exists and is running
            ContainerInspectResponse? container = null;
            try
            {
                container = await _dockerClient.Containers.InspectContainerAsync(containerId);
            }
            catch (DockerContainerNotFoundException)
            {
                // Container doesn't exist - silently return null
                return null;
            }

            // If container is not running, don't try to get stats
            if (container?.State?.Running != true)
            {
                return null;
            }

            ContainerStatsParameters statsParameters = new()
            {
                Stream = false // Get one-time stats, not streaming
            };

            Progress<ContainerStatsResponse> statsProgress = new();
            ContainerStatsResponse? lastStats = null;

            statsProgress.ProgressChanged += (sender, stats) =>
            {
                lastStats = stats;
            };

            await _dockerClient.Containers.GetContainerStatsAsync(
                containerId,
                statsParameters,
                statsProgress,
                CancellationToken.None
            );

            if (lastStats == null)
            {
                // No stats received, but container exists - just return null silently
                return null;
            }

            // Calculate CPU percentage
            ulong cpuDelta = lastStats.CPUStats.CPUUsage.TotalUsage - lastStats.PreCPUStats.CPUUsage.TotalUsage;
            ulong systemDelta = lastStats.CPUStats.SystemUsage - lastStats.PreCPUStats.SystemUsage;
            double cpuPercent = 0.0;
            if (systemDelta > 0 && cpuDelta > 0)
            {
                int cpuCount = lastStats.CPUStats.CPUUsage.PercpuUsage?.Count() ?? 1;
                cpuPercent = (cpuDelta / (double)systemDelta) * cpuCount * 100.0;
            }

            // Calculate memory usage
            ulong memoryUsage = lastStats.MemoryStats.Usage;
            ulong memoryLimit = lastStats.MemoryStats.Limit;
            double memoryPercent = memoryLimit > 0 ? (memoryUsage / (double)memoryLimit) * 100.0 : 0;

            // Calculate network I/O
            long networkRx = lastStats.Networks?.Values.Sum(n => (long)n.RxBytes) ?? 0;
            long networkTx = lastStats.Networks?.Values.Sum(n => (long)n.TxBytes) ?? 0;

            // Calculate disk I/O - simplified (DiskIO property might vary by Docker.DotNet version)
            long diskRead = 0L;
            long diskWrite = 0L;

            return new ContainerStatsDto(
                cpuPercent,
                memoryUsage,
                memoryLimit,
                memoryPercent,
                networkRx,
                networkTx,
                diskRead,
                diskWrite
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stats for container {ContainerId}", containerId);
            return null;
        }
    }
    /// <summary>
    /// Lists containers belonging to a specific Docker Compose project.
    /// Uses the com.docker.compose.project label to filter containers.
    /// </summary>
    /// <param name="projectName">The compose project name</param>
    /// <param name="showAll">Include stopped containers</param>
    /// <returns>List of containers with compose-specific metadata</returns>
    public async Task<List<ComposeServiceDto>> ListContainersByComposeProjectAsync(string projectName, bool showAll = true)
    {
        try
        {
            // Filter containers by compose project label
            var filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.project={projectName}"] = true
                }
            };

            IList<ContainerListResponse> containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = showAll,
                    Filters = filters
                });

            Dictionary<string, OneShotInfo> oneShots = await ResolveOneShotsRawAsync(containers);

            return containers.Select(c => {
                OneShotInfo? oneShot = oneShots.GetValueOrDefault(c.ID);
                // Use the actual container name (consistent with containers page)
                string containerName = NormalizeName(c.Names.FirstOrDefault());

                // Parse ports
                var ports = c.Ports?
                    .Where(p => p.PublicPort > 0)
                    .Select(p => $"{p.PublicPort}:{p.PrivatePort}")
                    .Distinct()
                    .ToList() ?? new List<string>();

                // Get health status from labels or status string
                string? health = null;
                if (c.Status?.Contains("(healthy)") == true) health = "healthy";
                else if (c.Status?.Contains("(unhealthy)") == true) health = "unhealthy";
                else if (c.Status?.Contains("(health:") == true) health = "starting";

                return new ComposeServiceDto(
                    Id: c.ID,
                    Name: containerName,
                    Image: c.Image,
                    State: c.State.ToEntityState().ToStateString(),
                    Status: c.Status ?? "",
                    Ports: ports,
                    Health: health,
                    IpAddress: c.NetworkSettings?.Networks?.Values.FirstOrDefault()?.IPAddress,
                    IsCrashLooping: _crashLoopDetection.IsContainerCrashLooping(c.ID),
                    ServiceName: OneShotDetector.GetLabel(c.Labels, OneShotDetector.ComposeServiceLabel),
                    IsOneShot: oneShot != null,
                    OneShotStatus: oneShot?.Status.ToString(),
                    ExitCode: oneShot?.ExitCode,
                    FinishedAt: oneShot?.FinishedAt
                );
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing containers for compose project {ProjectName}", projectName);
            return new List<ComposeServiceDto>();
        }
    }

    /// <summary>
    /// Gets Docker daemon version information
    /// </summary>
    public async Task<(string? version, string? apiVersion)> GetVersionAsync()
    {
        try
        {
            var versionResponse = await _dockerClient.System.GetVersionAsync();
            return (versionResponse.Version, versionResponse.APIVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Docker version");
            throw;
        }
    }

    /// <summary>
    /// Normalizes a docker container name by removing the leading '/' that the Docker API returns.
    /// Returns "unknown" if the provided name is null or whitespace.
    /// </summary>
    private static string NormalizeName(string? rawName)
        => string.IsNullOrWhiteSpace(rawName) ? "unknown" : rawName.TrimStart('/');

    // --- IDockerImageOperations -------------------------------------------------

    /// <inheritdoc />
    public async Task<IList<ImagesListResponse>> ListImagesRawAsync(CancellationToken ct = default)
    {
        try
        {
            return await _dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing images");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IList<ContainerListResponse>> ListContainersRawAsync(CancellationToken ct = default)
    {
        try
        {
            return await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing containers for image mapping");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteImageRawAsync(string id, bool force, CancellationToken ct = default)
    {
        // Bound the call: the Docker daemon can hang on some delete requests
        // (e.g. force-removing an image still referenced by a container). Without
        // this, the request would block on the client's default 100s timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await _dockerClient.Images.DeleteImageAsync(
                id, new ImageDeleteParameters { Force = force }, timeoutCts.Token);
            _logger.LogDebug("Image {ImageId} removed (force={Force})", id, force);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            _logger.LogWarning("Cannot remove image {ImageId}: not found", id);
            return false;
        }
        catch (DockerApiException ex)
        {
            // e.g. 409 conflict: the image is still referenced by a container.
            // Docker refuses this even with force, so report a clean failure.
            _logger.LogWarning("Cannot remove image {ImageId}: {Message}", id, ex.Message);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our safety timeout fired (the daemon hung), not a client cancellation.
            _logger.LogWarning("Timed out removing image {ImageId}", id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing image {ImageId}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ImagesPruneResponse> PruneImagesRawAsync(bool danglingOnly, CancellationToken ct = default)
    {
        try
        {
            // Docker's "dangling" filter: true => only untagged images,
            // false => every image not referenced by a container.
            var filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["dangling"] = new Dictionary<string, bool> { [danglingOnly ? "true" : "false"] = true }
            };

            return await _dockerClient.Images.PruneImagesAsync(
                new ImagesPruneParameters { Filters = filters }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pruning images (danglingOnly={DanglingOnly})", danglingOnly);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetContainerImageIdRawAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            ContainerInspectResponse container = await _dockerClient.Containers.InspectContainerAsync(containerId, ct);
            return container.Image;
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Cannot resolve image id for container {ContainerId}: not found", containerId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving image id for container {ContainerId}", containerId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, OneShotInfo>> ResolveOneShotsRawAsync(
        IEnumerable<ContainerListResponse> containers, CancellationToken ct = default)
    {
        try
        {
            // Only one-shot candidates are inspected (label or service_completed_successfully
            // dependency), so this stays cheap on hosts with many containers.
            return await OneShotDetector.ResolveAsync(containers, async id =>
            {
                ContainerInspectResponse inspect = await _dockerClient.Containers.InspectContainerAsync(id, ct);
                return new OneShotInspectData(
                    RestartPolicy: ToRestartPolicyName(inspect.HostConfig?.RestartPolicy?.Name),
                    ExitCode: inspect.State?.ExitCode,
                    FinishedAt: ParseDockerTimestamp(inspect.State?.FinishedAt));
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving one-shot containers");
            return new Dictionary<string, OneShotInfo>();
        }
    }

    private static string? ToRestartPolicyName(RestartPolicyKind? kind) => kind switch
    {
        RestartPolicyKind.No => "no",
        RestartPolicyKind.Always => "always",
        RestartPolicyKind.OnFailure => "on-failure",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        _ => null, // Undefined / unset
    };

    /// <summary>
    /// Docker reports "0001-01-01T00:00:00Z" for a container that never finished.
    /// </summary>
    private static DateTime? ParseDockerTimestamp(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed) && parsed.Year > 1
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
}
