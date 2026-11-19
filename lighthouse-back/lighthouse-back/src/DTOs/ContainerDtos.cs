namespace Lighthouse.DTOs;

public record ContainerDto(
    string Id,
    string Name,
    string Image,
    string Status,
    string State,
    DateTime Created,
    Dictionary<string, string>? Labels,
    List<string>? Ports = null,
    string? IpAddress = null,
    bool IsCrashLooping = false,
    // One-shot container (init / copy / migration task expected to exit), see OneShotDetector.
    bool IsOneShot = false,
    // One-shot run status: Pending, Running, Completed or Failed (null when not a one-shot).
    string? OneShotStatus = null,
    // Last exit code (one-shots only).
    long? ExitCode = null,
    // When the last run finished, UTC (one-shots only, null while running / never run).
    DateTime? FinishedAt = null
);

public record ContainerDetailsDto(
    string Id,
    string Name,
    string Image,
    string Status,
    string State,
    DateTime Created,
    Dictionary<string, string>? Labels,
    Dictionary<string, string>? Env,
    List<MountDto>? Mounts,
    List<string>? Networks,
    Dictionary<string, string>? PortDetails,
    string? IpAddress = null,
    List<string>? Ports = null
);

public record MountDto(
    string Type,
    string Source,
    string Destination,
    bool ReadOnly
);

public record ContainerStatsDto(
    double CpuPercentage,
    ulong MemoryUsage,
    ulong MemoryLimit,
    double MemoryPercentage,
    long NetworkRx,
    long NetworkTx,
    long DiskRead,
    long DiskWrite
);

/// <summary>
/// Response for individual container update check.
/// </summary>
public record ContainerUpdateCheckResponse(
    string ContainerId,
    string ContainerName,
    string Image,
    bool UpdateAvailable,
    bool IsComposeManaged,
    string? ProjectName,
    string? LocalDigest,
    string? RemoteDigest,
    bool RequiredPull,
    string? Error,
    string? ContainerState = null
);
