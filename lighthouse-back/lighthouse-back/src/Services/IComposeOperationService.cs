using Lighthouse.DTOs;

namespace Lighthouse.Services;

/// <summary>
/// Service for performing operations on compose projects
/// </summary>
public interface IComposeOperationService
{
    /// <summary>
    /// Creates and starts a compose project from file (docker compose -f file up -d)
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="composeFilePath">Path to the compose file (required for 'up' command)</param>
    /// <param name="build">Whether to build images before starting</param>
    /// <param name="forceRecreate">Whether to recreate containers even if their configuration and image are unchanged</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result</returns>
    Task<OperationResult> UpAsync(string projectName, string? composeFilePath = null, bool build = false, bool forceRecreate = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes a compose project (docker compose down)
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="removeVolumes">Whether to remove volumes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result</returns>
    Task<OperationResult> DownAsync(string projectName, bool removeVolumes = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts a compose project (docker compose restart)
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result</returns>
    Task<OperationResult> RestartAsync(string projectName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a compose project without removing containers (docker compose stop)
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result</returns>
    Task<OperationResult> StopAsync(string projectName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a previously stopped compose project (docker compose start)
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result</returns>
    Task<OperationResult> StartAsync(string projectName, CancellationToken cancellationToken = default);
}
