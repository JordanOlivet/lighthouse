using System.Diagnostics;
using System.Text;

namespace Lighthouse.Services;

/// <summary>
/// Centralizes Docker Compose command execution
/// </summary>
public class DockerCommandExecutorService
{
    private readonly ILogger<DockerCommandExecutorService> _logger;
    private bool? _isComposeV2;

    public DockerCommandExecutorService(ILogger<DockerCommandExecutorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detects if docker compose v2 (docker compose) or v1 (docker-compose) is available
    /// </summary>
    public async Task<bool> IsComposeV2Available()
    {
        if (_isComposeV2.HasValue)
        {
            return _isComposeV2.Value;
        }

        try
        {
            // Try docker compose version (v2)
            ProcessStartInfo psi = new()
            {
                FileName = "docker",
                Arguments = "compose version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = "/"
            };

            using Process? process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                {
                    _isComposeV2 = true;
                    _logger.LogDebug("Docker Compose v2 detected");
                    return true;
                }
            }

            // Fall back to docker-compose (v1)
            psi.FileName = "docker-compose";
            psi.Arguments = "version";

            using Process? processV1 = Process.Start(psi);
            if (processV1 != null)
            {
                await processV1.WaitForExitAsync();
                if (processV1.ExitCode == 0)
                {
                    _isComposeV2 = false;
                    _logger.LogDebug("Docker Compose v1 detected");
                    return false;
                }
            }

            throw new InvalidOperationException("Docker Compose not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting docker compose version");
            throw;
        }
    }

    /// <summary>
    /// Runs a process to completion, capturing stdout/stderr and optionally streaming each
    /// line to callbacks and/or feeding stdin. This is the single implementation shared by
    /// all the Execute* helpers below.
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo psi,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        string? stdinInput = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder output = new();
        StringBuilder error = new();

        using Process? process = Process.Start(psi);
        if (process == null)
        {
            return (-1, "", $"Failed to start process: {psi.FileName}");
        }

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;
            output.AppendLine(e.Data);
            try
            {
                onOutputLine?.Invoke(e.Data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in output callback for command: {Command}", psi.FileName);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;
            error.AppendLine(e.Data);
            try
            {
                onErrorLine?.Invoke(e.Data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in error callback for command: {Command}", psi.FileName);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdinInput != null)
        {
            await process.StandardInput.WriteAsync(stdinInput);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Executes a docker compose command
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteComposeCommandAsync(
        string workingDirectory,
        string arguments,
        string? composeFile = null,
        CancellationToken cancellationToken = default)
    {
        bool isV2 = await IsComposeV2Available();

        // Log Docker auth config state for diagnostic purposes
        string? dockerConfigEnv = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        string? homeEnv = Environment.GetEnvironmentVariable("HOME");
        string configPath = !string.IsNullOrEmpty(dockerConfigEnv)
            ? Path.Combine(dockerConfigEnv, "config.json")
            : !string.IsNullOrEmpty(homeEnv)
                ? Path.Combine(homeEnv, ".docker", "config.json")
                : "";
        bool configExists = !string.IsNullOrEmpty(configPath) && File.Exists(configPath);

        _logger.LogDebug(
            "Docker auth state - DOCKER_CONFIG: {DockerConfig}, HOME: {Home}, config.json exists: {ConfigExists}, path: {ConfigPath}",
            dockerConfigEnv ?? "(not set)",
            homeEnv ?? "(not set)",
            configExists,
            configPath);

        // Add -f option if compose file is specified. The path is passed as given (not reduced to
        // its file name): compose derives the project directory from it, and resolving a bare file
        // name against the working directory would follow symlinks back to the container path (#219).
        string fileArg = "";
        if (!string.IsNullOrEmpty(composeFile))
        {
            fileArg = $"-f \"{composeFile}\" ";
        }

        ProcessStartInfo psi = new()
        {
            FileName = isV2 ? "docker" : "docker-compose",
            Arguments = isV2 ? $"compose {fileArg}{arguments}" : $"{fileArg}{arguments}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        (int exitCode, string outputStr, string errorStr) =
            await RunProcessAsync(psi, cancellationToken: cancellationToken);

        _logger.LogDebug(
            "Compose command executed: {Command}, Exit Code: {ExitCode}, Output: {Output}, Error: {Error}",
            arguments,
            exitCode,
            outputStr,
            errorStr
        );

        return (exitCode, outputStr, errorStr);
    }

    /// <summary>
    /// Executes a generic command (docker or any other CLI command)
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string command,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        (int exitCode, string outputStr, string errorStr) =
            await RunProcessAsync(psi, cancellationToken: cancellationToken);

        _logger.LogDebug(
            "Command executed: {Command} {Arguments}, Exit Code: {ExitCode}",
            command,
            arguments,
            exitCode
        );

        return (exitCode, outputStr, errorStr);
    }

    /// <summary>
    /// Executes a command with streaming output callbacks for real-time progress updates.
    /// Each line of stdout/stderr is passed to the respective callback as it arrives.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteWithStreamingAsync(
        string command,
        string arguments,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set the working directory so docker compose discovers the adjacent .env file
        // (compose loads .env from the current working directory, not the -f file's directory).
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        (int exitCode, string outputStr, string errorStr) =
            await RunProcessAsync(psi, onOutputLine, onErrorLine, cancellationToken: cancellationToken);

        _logger.LogDebug(
            "Streaming command executed: {Command} {Arguments}, Exit Code: {ExitCode}",
            command,
            arguments,
            exitCode
        );

        return (exitCode, outputStr, errorStr);
    }

    /// <summary>
    /// Executes a command with input provided via stdin (useful for secure password passing)
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteWithStdinAsync(
        string command,
        string arguments,
        string stdinInput,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        (int exitCode, string outputStr, string errorStr) =
            await RunProcessAsync(psi, stdinInput: stdinInput, cancellationToken: cancellationToken);

        // Don't log sensitive data like passwords
        _logger.LogDebug(
            "Command with stdin executed: {Command} {Arguments}, Exit Code: {ExitCode}",
            command,
            arguments,
            exitCode
        );

        return (exitCode, outputStr, errorStr);
    }
}
