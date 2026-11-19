using System.Text.Json.Serialization;

namespace Lighthouse.DTOs;

// ============================================
// Compose File DTOs
// ============================================

/// <summary>
/// Basic compose file info (for list views)
/// </summary>
public record ComposeFileDto(
    int Id,
    string FileName,
    string FullPath,
    string Directory,
    long Size,
    DateTime LastModified,
    DateTime LastScanned,
    int ComposePathId,
    bool IsDiscovered
);

/// <summary>
/// Compose file with content
/// </summary>
public record ComposeFileContentDto(
    int Id,
    string FileName,
    string FullPath,
    string Content,
    string ETag,
    DateTime LastModified
);

/// <summary>
/// Request to create a new compose file
/// </summary>
public record CreateComposeFileRequest(
    string FilePath,
    string Content
);

/// <summary>
/// Request to update a compose file
/// </summary>
public record UpdateComposeFileRequest(
    string Content,
    string ETag // For optimistic locking
);

// ============================================
// Compose Project File Editing DTOs
// ============================================

/// <summary>
/// Well-known kinds of editable files belonging to a compose project.
/// </summary>
public static class ProjectFileKind
{
    public const string Compose = "compose";
    public const string Env = "env";
}

/// <summary>
/// An editable file of a compose project (the compose file itself or its adjacent .env).
/// </summary>
/// <param name="Kind">One of <see cref="ProjectFileKind"/>.</param>
/// <param name="FileName">File name only (no path) — the server never exposes or accepts paths.</param>
/// <param name="Content">Raw file content, null when the file does not exist.</param>
/// <param name="ETag">SHA-256 of the file bytes for optimistic locking, null when the file does not exist.</param>
/// <param name="Exists">Whether the file currently exists on disk.</param>
public record ProjectFileDto(
    string Kind,
    string FileName,
    string? Content,
    // Force the JSON name to "etag": the default camelCase policy would turn "ETag" into "eTag"
    // (only the first char is lowercased), which the client reads as undefined -> sends back a
    // null ETag -> every save is a false conflict.
    [property: JsonPropertyName("etag")] string? ETag,
    bool Exists
);

/// <summary>
/// All editable files of a compose project.
/// </summary>
public record ProjectFilesResponseDto(
    string ProjectName,
    List<ProjectFileDto> Files
);

/// <summary>
/// Request to update (or create, for .env) an editable project file.
/// </summary>
/// <param name="Kind">One of <see cref="ProjectFileKind"/>.</param>
/// <param name="Content">New file content.</param>
/// <param name="ETag">ETag from the last read; required when the file exists (optimistic locking).</param>
public record UpdateProjectFileRequest(
    string Kind,
    string Content,
    // Symmetry with ProjectFileDto: accept "etag" explicitly (request binding is already
    // case-insensitive, but keep the contract unambiguous).
    [property: JsonPropertyName("etag")] string? ETag
);

// ============================================
// Compose Project DTOs
// ============================================

/// <summary>
/// Compose project information
/// </summary>
public record ComposeProjectDto(
    string Name,
    string Path,
    string State, // up, down, degraded
    List<ComposeServiceDto> Services,
    List<string> ComposeFiles,
    DateTime? LastUpdated,

    // New fields for compose discovery revamp
    // Path to the primary compose file associated with this project (null if not found)
    string? ComposeFilePath = null,
    // Indicates whether a compose file was found for this project
    bool HasComposeFile = false,
    // Warning message if project has issues (e.g., "No compose file found for this project")
    string? Warning = null,
    // Dictionary of available actions and whether they can be performed.
    // Actions that require a compose file (up, build, recreate, pull) will be false if HasComposeFile is false.
    // Actions that work with project name only (start, stop, restart, pause, logs, ps, down) are always true for running projects.
    Dictionary<string, bool>? AvailableActions = null,
    // Number of services with available updates (from cache, null if not checked)
    int? ServicesWithUpdates = null,
    // When the last update check was performed (from cache, null if not checked)
    DateTime? LastUpdateCheck = null,
    // Whether any service in this project is crash-looping
    bool IsCrashLooping = false,
    // Whether automatic updates are enabled for this project (false if the
    // compose file is flagged with x-auto-update: false). Defaults to true.
    bool AutoUpdateEnabled = true,
    // Whether the current user may edit this project's compose file. Only populated by the
    // single-project detail endpoint (the list endpoint leaves it false to stay cheap).
    bool CanEdit = false
);

/// <summary>
/// Service within a compose project (detailed view)
/// </summary>
public record ComposeServiceDto(
    string Id,
    string Name,
    string? Image,
    string State, // running, exited, restarting, etc.
    string Status, // Up xx minutes
    List<string> Ports,
    string? Health,
    string? IpAddress = null,
    bool IsCrashLooping = false,
    // Compose service name (com.docker.compose.service label); Name is the container name.
    string? ServiceName = null,
    // One-shot service (init / copy / migration task expected to exit), see OneShotDetector.
    bool IsOneShot = false,
    // One-shot run status: Pending, Running, Completed or Failed (null when not a one-shot).
    string? OneShotStatus = null,
    // Last exit code (one-shots only).
    long? ExitCode = null,
    // When the last run finished, UTC (one-shots only, null while running / never run).
    DateTime? FinishedAt = null
);

/// <summary>
/// Service status (simplified view - for docker compose ps)
/// </summary>
public record ComposeServiceStatusDto(
    string Name,
    string State,
    string Status
);

/// <summary>
/// Detailed information about a compose project
/// </summary>
public record ComposeProjectDetailsDto(
    string Name,
    string Path,
    bool IsRunning,
    int TotalServices,
    int RunningServices,
    int StoppedServices,
    List<ComposeServiceDto> Services
);

/// <summary>
/// Request to start a compose project
/// </summary>
public record ComposeUpRequest(
    bool Build = false,
    bool Detach = true,
    bool ForceRecreate = false
);

/// <summary>
/// Request to stop a compose project
/// </summary>
public record ComposeDownRequest(
    bool RemoveVolumes = false,
    string? RemoveImages = null // "all" or "local"
);

/// <summary>
/// Response for compose up/down operations
/// </summary>
public record ComposeOperationResponse(
    string OperationId,
    string Status,
    string Message
);

// ============================================
// Compose Template DTOs
// ============================================

/// <summary>
/// Compose file template
/// </summary>
public record ComposeTemplateDto(
    string Id,
    string Name,
    string Description,
    string Content,
    List<string> Tags
);

// ============================================
// Compose Path DTOs
// ============================================

/// <summary>
/// Compose path information
/// </summary>
public record ComposePathDto(
    int Id,
    string Path,
    bool IsReadOnly,
    bool IsEnabled,
    int FileCount,
    DateTime CreatedAt
);

/// <summary>
/// Request to add a compose path
/// </summary>
public record AddComposePathRequest(
    string Path,
    bool IsReadOnly = false
);

// ============================================
// Compose File Validation DTOs
// ============================================

/// <summary>
/// Result of compose file validation
/// </summary>
public class ComposeValidationResult
{
    public bool IsValid { get; set; }
    public bool YamlValid { get; set; }
    public string? YamlError { get; set; }
    public List<string> Warnings { get; set; } = new();
    public int ServiceCount { get; set; }
}

/// <summary>
/// Request to duplicate a compose file
/// </summary>
public record DuplicateFileRequest(
    string? NewFileName = null
);

// ============================================
// Compose File Details DTOs (parsed content)
// ============================================

/// <summary>
/// Parsed compose file details with structured information
/// </summary>
public record ComposeFileDetailsDto(
    string ProjectName,
    string? Version,
    Dictionary<string, ServiceDetailsDto> Services,
    Dictionary<string, NetworkDetailsDto>? Networks,
    Dictionary<string, VolumeDetailsDto>? Volumes
);

/// <summary>
/// Service details from compose file
/// </summary>
public record ServiceDetailsDto(
    string Name,
    string? Image,
    string? Build,
    List<string>? Ports,
    Dictionary<string, string>? Environment,
    Dictionary<string, string>? Labels,
    List<string>? Volumes,
    List<string>? DependsOn,
    string? Restart,
    Dictionary<string, string>? Networks
);

/// <summary>
/// Network details from compose file
/// </summary>
public record NetworkDetailsDto(
    string Name,
    string? Driver,
    bool? External,
    Dictionary<string, object>? DriverOpts,
    Dictionary<string, string>? Labels
);

/// <summary>
/// Volume details from compose file
/// </summary>
public record VolumeDetailsDto(
    string Name,
    string? Driver,
    bool? External,
    Dictionary<string, object>? DriverOpts,
    Dictionary<string, string>? Labels
);

// ============================================
// Operation Result DTOs
// ============================================

/// <summary>
/// Result of a compose operation (for internal use)
/// </summary>
public record OperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Output { get; init; }
    public string? Error { get; init; }
}
