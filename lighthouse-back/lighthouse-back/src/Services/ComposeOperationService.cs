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
    private readonly ILogger<ComposeOperationService> _logger;

    public ComposeOperationService(
        DockerCommandExecutorService dockerExecutor,
        IComposeDiscoveryService discoveryService,
        IComposeEnvFileResolver envFileResolver,
        ILogger<ComposeOperationService> logger)
    {
        _dockerExecutor = dockerExecutor;
        _discoveryService = discoveryService;
        _envFileResolver = envFileResolver;
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

            // 'up' requires compose file - validation is done by controller via GetUnifiedProjectListAsync
            // We don't validate against Docker projects because "Not Started" projects don't exist in Docker yet
            if (string.IsNullOrEmpty(composeFilePath))
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Cannot execute 'up' for project '{projectName}': No compose file provided",
                    Output = null,
                    Error = "The 'up' command requires a compose file. Use 'start' to resume existing stopped containers."
                };
            }

            // Validate compose file exists
            if (!File.Exists(composeFilePath))
            {
                _logger.LogWarning("Compose file not found: {ComposeFile}", composeFilePath);
                return new OperationResult
                {
                    Success = false,
                    Message = $"Compose file not found: {composeFilePath}",
                    Output = null,
                    Error = "Compose file does not exist"
                };
            }

            // Execute docker compose -f <file> [--env-file ...] up -d [--build]
            string workingDirectory = Path.GetDirectoryName(composeFilePath) ?? "/";
            string envFileArgs = await _envFileResolver.BuildEnvFileArgsAsync(workingDirectory, cancellationToken);
            string buildArg = build ? " --build" : "";
            string forceRecreateArg = forceRecreate ? " --force-recreate" : "";
            string arguments = $"{envFileArgs}up -d{buildArg}{forceRecreateArg}";

            _logger.LogDebug(
                "Executing 'up' with compose file '{ComposeFile}' in '{WorkingDir}'",
                composeFilePath, workingDirectory);

            (int exitCode, string? output, string? error) = await _dockerExecutor.ExecuteComposeCommandAsync(
                workingDirectory: workingDirectory,
                arguments: arguments,
                composeFile: composeFilePath,
                cancellationToken: cancellationToken
            );

            bool success = exitCode == 0;
            string message = success
                ? $"Project '{projectName}' created/started successfully"
                : $"Failed to create/start project '{projectName}'";

            if (success)
            {
                _logger.LogDebug("Project {ProjectName} up successful", projectName);
            }
            else
            {
                _logger.LogWarning("Failed to up project {ProjectName}: {Error}", projectName, error);
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
