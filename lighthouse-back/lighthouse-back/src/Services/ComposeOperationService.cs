using Docker.DotNet.Models;
using Lighthouse.DTOs;
using Lighthouse.Utils;

namespace Lighthouse.Services;

/// <summary>
/// Service for performing operations on compose projects
/// </summary>
public class ComposeOperationService : IComposeOperationService
{
    private readonly DockerCommandExecutorService _dockerExecutor;
    private readonly IComposeDiscoveryService _discoveryService;
    private readonly IComposeEnvFileResolver _envFileResolver;
    private readonly IComposeHostPathService _hostPathService;
    private readonly IDockerImageOperations _dockerOps;
    private readonly ILogger<ComposeOperationService> _logger;

    public ComposeOperationService(
        DockerCommandExecutorService dockerExecutor,
        IComposeDiscoveryService discoveryService,
        IComposeEnvFileResolver envFileResolver,
        IComposeHostPathService hostPathService,
        IDockerImageOperations dockerOps,
        ILogger<ComposeOperationService> logger)
    {
        _dockerExecutor = dockerExecutor;
        _discoveryService = discoveryService;
        _envFileResolver = envFileResolver;
        _hostPathService = hostPathService;
        _dockerOps = dockerOps;
        _logger = logger;
    }

    /// <summary>
    /// Rejects project names that are not on the safe-character allowlist before they
    /// are passed to the docker CLI. Returns a failure result to short-circuit, or null
    /// when the name is valid.
    /// </summary>
    private OperationResult? RejectIfInvalidName(string projectName)
    {
        if (ComposeProjectNameValidator.IsValid(projectName))
            return null;

        _logger.LogWarning("Rejected compose operation for invalid project name: {ProjectName}", projectName);
        return new OperationResult
        {
            Success = false,
            Message = $"Invalid project name '{projectName}'",
            Output = null,
            Error = "Project name contains invalid characters"
        };
    }

    /// <summary>
    /// Validates that a project exists in Docker
    /// </summary>
    private async Task<bool> ValidateProjectExistsAsync(string projectName)
    {
        try
        {
            // Get project from discovery (this uses cache)
            var projects = await _discoveryService.GetAllProjectsAsync();
            var project = projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (project == null)
            {
                _logger.LogWarning("Project {ProjectName} not found in discovery", projectName);
                return false;
            }

            _logger.LogDebug("Project {ProjectName} found with {FileCount} compose files", projectName, project.ComposeFiles.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating project {ProjectName}", projectName);
            return false;
        }
    }

    public async Task<OperationResult> UpAsync(string projectName, string? composeFilePath = null, bool build = false, bool forceRecreate = false, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug(
                "Creating/starting compose project with 'up': {ProjectName}, ComposeFile: {ComposeFile}, Build: {Build}, ForceRecreate: {ForceRecreate}",
                projectName, composeFilePath ?? "none", build, forceRecreate);

            string buildArg = build ? " --build" : "";
            string forceRecreateArg = forceRecreate ? " --force-recreate" : "";

            return await RunUpAsync(
                projectName,
                composeFilePath,
                $"up -d{buildArg}{forceRecreateArg}",
                $"Project '{projectName}' created/started successfully",
                $"Failed to create/start project '{projectName}'",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing 'up' for project {ProjectName}", projectName);
            return new OperationResult
            {
                Success = false,
                Message = "Unexpected error occurred",
                Output = null,
                Error = ex.Message
            };
        }
    }

    public async Task<OperationResult> RerunOneShotAsync(string projectName, string? composeFilePath, string serviceName, CancellationToken cancellationToken = default)
    {
        try
        {
            OperationResult? nameError = RejectIfInvalidName(projectName);
            if (nameError != null) return nameError;

            if (!ComposeProjectNameValidator.IsValid(serviceName))
            {
                _logger.LogWarning("Rejected one-shot re-run for invalid service name: {ServiceName}", serviceName);
                return Fail($"Invalid service name '{serviceName}'", "Service name contains invalid characters");
            }

            List<ContainerListResponse> projectContainers = (await _dockerOps.ListContainersRawAsync(cancellationToken))
                .Where(c => string.Equals(OneShotDetector.GetLabel(c.Labels, OneShotDetector.ComposeProjectLabel),
                    projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Dictionary<string, OneShotInfo> oneShots =
                await _dockerOps.ResolveOneShotsRawAsync(projectContainers, cancellationToken) ?? new();

            List<string>? rerunTargets = OneShotDetector.GetRerunTargets(projectContainers, oneShots, projectName, serviceName);
            if (rerunTargets == null)
            {
                return Fail(
                    $"Service '{serviceName}' of project '{projectName}' is not a one-shot service",
                    "Only one-shot services can be re-run");
            }

            string targets = string.Join(" ", rerunTargets);

            _logger.LogInformation(
                "Re-running one-shot {ServiceName} of project {ProjectName} (targets: [{Targets}])",
                serviceName, projectName, string.Join(", ", rerunTargets));

            return await RunUpAsync(
                projectName,
                composeFilePath,
                $"up -d --force-recreate {targets}",
                $"One-shot '{serviceName}' re-run successfully",
                $"Failed to re-run one-shot '{serviceName}'",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-running one-shot {ServiceName} of project {ProjectName}", serviceName, projectName);
            return Fail("Unexpected error occurred", ex.Message);
        }
    }

    private static OperationResult Fail(string message, string error) => new()
    {
        Success = false,
        Message = message,
        Output = null,
        Error = error
    };

    /// <summary>
    /// Runs <c>docker compose [--env-file ...] &lt;upArguments&gt;</c> against the project's compose
    /// file, after the checks every <c>up</c> needs (file present, no relative-mount conflict).
    /// </summary>
    private async Task<OperationResult> RunUpAsync(
        string projectName,
        string? composeFilePath,
        string upArguments,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        // 'up' requires compose file - validation is done by controller via GetUnifiedProjectListAsync
        // We don't validate against Docker projects because "Not Started" projects don't exist in Docker yet
        if (string.IsNullOrEmpty(composeFilePath))
        {
            return Fail(
                $"Cannot execute 'up' for project '{projectName}': No compose file provided",
                "The 'up' command requires a compose file. Use 'start' to resume existing stopped containers.");
        }

        // Validate compose file exists
        if (!File.Exists(composeFilePath))
        {
            _logger.LogWarning("Compose file not found: {ComposeFile}", composeFilePath);
            return Fail($"Compose file not found: {composeFilePath}", "Compose file does not exist");
        }

        // Refuse to remount relative bind mounts on empty host folders when the host path
        // of the compose directory is unknown (#219).
        string? mountConflict = await _hostPathService.GetRelativeMountConflictAsync(projectName, composeFilePath, cancellationToken);
        if (mountConflict != null)
        {
            _logger.LogWarning("Refused 'up' for project {ProjectName}: {Reason}", projectName, mountConflict);
            return Fail(mountConflict, mountConflict);
        }

        // Run from the compose file's host path so relative paths resolve as on the host (#219).
        composeFilePath = await _hostPathService.ToExecutionPathAsync(composeFilePath, cancellationToken);

        // Execute docker compose -f <file> [--env-file ...] <upArguments>
        string workingDirectory = Path.GetDirectoryName(composeFilePath) ?? "/";
        string envFileArgs = await _envFileResolver.BuildEnvFileArgsAsync(workingDirectory, cancellationToken);
        string arguments = $"{envFileArgs}{upArguments}";

        _logger.LogDebug(
            "Executing '{Arguments}' with compose file '{ComposeFile}' in '{WorkingDir}'",
            upArguments, composeFilePath, workingDirectory);

        (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
            workingDirectory: workingDirectory,
            arguments: arguments,
            composeFile: composeFilePath,
            cancellationToken: cancellationToken
        );

        bool success = exitCode == 0;
        if (success)
        {
            _logger.LogDebug("Project {ProjectName} '{Arguments}' successful", projectName, upArguments);
        }
        else
        {
            _logger.LogWarning("Failed '{Arguments}' for project {ProjectName}: {Error}", upArguments, projectName, error);
        }

        return new OperationResult
        {
            Success = success,
            Message = success ? successMessage : failureMessage,
            Output = output,
            Error = error
        };
    }

    public async Task<OperationResult> DownAsync(string projectName, bool removeVolumes = false, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug(
                "Stopping compose project: {ProjectName}, Remove volumes: {RemoveVolumes}",
                projectName,
                removeVolumes
            );

            OperationResult? nameError = RejectIfInvalidName(projectName);
            if (nameError != null) return nameError;

            // Validate project exists
            bool projectExists = await ValidateProjectExistsAsync(projectName);
            if (!projectExists)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Project '{projectName}' not found",
                    Output = null,
                    Error = "Project not found in Docker"
                };
            }

            string volumesArg = removeVolumes ? "--volumes" : "";
            string arguments = $"-p \"{projectName}\" down {volumesArg}".Trim();

            (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
                workingDirectory: "/",
                arguments: arguments,
                cancellationToken: cancellationToken
            );

            bool success = exitCode == 0;
            string message = success
                ? $"Project '{projectName}' stopped successfully"
                : $"Failed to stop project '{projectName}'";

            if (success)
            {
                _logger.LogDebug("Project {ProjectName} stopped successfully", projectName);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to stop project {ProjectName}: Exit code {ExitCode}, Error: {Error}",
                    projectName,
                    exitCode,
                    error
                );
            }

            return new OperationResult
            {
                Success = success,
                Message = message,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping project {ProjectName}", projectName);
            return new OperationResult
            {
                Success = false,
                Message = "Unexpected error occurred",
                Output = null,
                Error = ex.Message
            };
        }
    }

    public async Task<OperationResult> RestartAsync(string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Restarting compose project: {ProjectName}", projectName);

            OperationResult? nameError = RejectIfInvalidName(projectName);
            if (nameError != null) return nameError;

            // Validate project exists
            bool projectExists = await ValidateProjectExistsAsync(projectName);
            if (!projectExists)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Project '{projectName}' not found",
                    Output = null,
                    Error = "Project not found in Docker"
                };
            }

            string arguments = $"-p \"{projectName}\" restart";

            (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
                workingDirectory: "/",
                arguments: arguments,
                cancellationToken: cancellationToken
            );

            bool success = exitCode == 0;
            string message = success
                ? $"Project '{projectName}' restarted successfully"
                : $"Failed to restart project '{projectName}'";

            if (success)
            {
                _logger.LogDebug("Project {ProjectName} restarted successfully", projectName);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to restart project {ProjectName}: Exit code {ExitCode}, Error: {Error}",
                    projectName,
                    exitCode,
                    error
                );
            }

            return new OperationResult
            {
                Success = success,
                Message = message,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting project {ProjectName}", projectName);
            return new OperationResult
            {
                Success = false,
                Message = "Unexpected error occurred",
                Output = null,
                Error = ex.Message
            };
        }
    }

    public async Task<OperationResult> StopAsync(string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Stopping compose project (without removing): {ProjectName}", projectName);

            OperationResult? nameError = RejectIfInvalidName(projectName);
            if (nameError != null) return nameError;

            // Validate project exists
            bool projectExists = await ValidateProjectExistsAsync(projectName);
            if (!projectExists)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Project '{projectName}' not found",
                    Output = null,
                    Error = "Project not found in Docker"
                };
            }

            string arguments = $"-p \"{projectName}\" stop";

            (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
                workingDirectory: "/",
                arguments: arguments,
                cancellationToken: cancellationToken
            );

            bool success = exitCode == 0;
            string message = success
                ? $"Project '{projectName}' stopped successfully"
                : $"Failed to stop project '{projectName}'";

            if (success)
            {
                _logger.LogDebug("Project {ProjectName} stopped successfully (containers not removed)", projectName);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to stop project {ProjectName}: Exit code {ExitCode}, Error: {Error}",
                    projectName,
                    exitCode,
                    error
                );
            }

            return new OperationResult
            {
                Success = success,
                Message = message,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping project {ProjectName}", projectName);
            return new OperationResult
            {
                Success = false,
                Message = "Unexpected error occurred",
                Output = null,
                Error = ex.Message
            };
        }
    }

    public async Task<OperationResult> StartAsync(string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting previously stopped project: {ProjectName}", projectName);

            OperationResult? nameError = RejectIfInvalidName(projectName);
            if (nameError != null) return nameError;

            // Validate project exists
            bool projectExists = await ValidateProjectExistsAsync(projectName);
            if (!projectExists)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Project '{projectName}' not found",
                    Output = null,
                    Error = "Project not found in Docker"
                };
            }

            string arguments = $"-p \"{projectName}\" start";

            (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
                workingDirectory: "/",
                arguments: arguments,
                cancellationToken: cancellationToken
            );

            bool success = exitCode == 0;
            string message = success
                ? $"Project '{projectName}' started successfully"
                : $"Failed to start project '{projectName}'";

            if (success)
            {
                _logger.LogDebug("Project {ProjectName} started successfully", projectName);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to start project {ProjectName}: Exit code {ExitCode}, Error: {Error}",
                    projectName,
                    exitCode,
                    error
                );
            }

            return new OperationResult
            {
                Success = success,
                Message = message,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting project {ProjectName}", projectName);
            return new OperationResult
            {
                Success = false,
                Message = "Unexpected error occurred",
                Output = null,
                Error = ex.Message
            };
        }
    }
}
