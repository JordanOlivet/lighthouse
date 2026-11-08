namespace Lighthouse.DTOs;

/// <summary>
/// Response containing application update check results.
/// </summary>
public record AppUpdateCheckResponse(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl,
    List<ReleaseInfo> Changelog,
    ChangelogSummary Summary,
    // Published dates for release versions
    DateTime? CurrentVersionPublishedAt = null,
    DateTime? LatestVersionPublishedAt = null,
    // Fields for dev version update checks
    bool IsDevVersion = false,
    string? LocalDigest = null,
    string? RemoteDigest = null,
    DateTime? LocalCreatedAt = null,
    DateTime? RemoteCreatedAt = null
);

/// <summary>
/// Information about a single release.
/// </summary>
public record ReleaseInfo(
    string Version,
    string TagName,
    DateTime PublishedAt,
    string ReleaseNotes,
    string ReleaseUrl,
    bool IsBreakingChange,
    bool IsSecurityFix,
    bool IsPreRelease
);

/// <summary>
/// Summary of changes between current and latest version.
/// </summary>
public record ChangelogSummary(
    int TotalReleases,
    bool HasBreakingChanges,
    bool HasSecurityFixes,
    bool HasPreReleases
);

/// <summary>
/// Response after triggering an application update.
/// </summary>
/// <param name="Success">Whether the update completed.</param>
/// <param name="Message">Outcome message (user-facing error on failure).</param>
/// <param name="OperationId">Id of the tracked operation, when one was created.</param>
/// <param name="RateLimited">
/// True when the update failed because the registry kept answering 429 (toomanyrequests) after the
/// pull retries were exhausted. Transient by nature, so callers may retry later.
/// </param>
public record UpdateTriggerResponse(
    bool Success,
    string Message,
    string? OperationId,
    bool RateLimited = false
);

/// <summary>
/// Request to trigger an application update.
/// </summary>
public record UpdateTriggerRequest(
    bool Force = false
);

/// <summary>
/// Maintenance mode notification data sent via SignalR.
/// </summary>
/// <param name="IsActive">Whether maintenance mode is active.</param>
/// <param name="Message">Message to display to users.</param>
/// <param name="EstimatedEndTime">Estimated time when maintenance will end.</param>
/// <param name="GracePeriodSeconds">Grace period in seconds before update starts.</param>
/// <param name="PreUpdateInstanceId">Instance ID before the update, used to detect when new instance is ready.</param>
public record MaintenanceModeNotification(
    bool IsActive,
    string Message,
    DateTime? EstimatedEndTime,
    int GracePeriodSeconds,
    string? PreUpdateInstanceId = null
);

/// <summary>
/// Response containing current update status.
/// </summary>
public record UpdateStatusResponse(
    bool IsUpdateInProgress
);

// ============================================
// Compose Project Update DTOs
// ============================================

/// <summary>
/// Response containing project update check results.
/// </summary>
public record ProjectUpdateCheckResponse(
    string ProjectName,
    List<ImageUpdateStatus> Images,
    bool HasUpdates,
    DateTime LastChecked
);

/// <summary>
/// Status of a single image's update availability.
/// </summary>
public record ImageUpdateStatus(
    string Image,
    string ServiceName,
    string HostArchitecture,
    string? LocalDigest,
    string? RemoteDigest,
    DateTime? LocalCreatedAt,
    DateTime? RemoteCreatedAt,
    bool UpdateAvailable,
    bool MultiArchSupported,
    string? UpdatePolicy,
    bool IsLocalBuild,
    bool IsPinnedDigest,
    string? Error,
    string? ContainerState = null
);

/// <summary>
/// Request to update project services.
/// </summary>
public record ProjectUpdateRequest(
    List<string>? Services = null,
    bool UpdateAll = false,
    bool RestartFullProject = true,
    bool RestartAfterUpdate = true
);

/// <summary>
/// Request to update a container.
/// </summary>
public record ContainerUpdateRequest(
    bool RestartAfterUpdate = true
);

/// <summary>
/// Summary of a project's update status.
/// </summary>
public record ProjectUpdateSummary(
    string ProjectName,
    int ServicesWithUpdates,
    DateTime? LastChecked,
    bool HasRunningServices = false
);

/// <summary>
/// Response for update all projects operation.
/// </summary>
public record UpdateAllResponse(
    string OperationId,
    List<string> ProjectsToUpdate,
    string Status
);

/// <summary>
/// Information about a parsed image reference.
/// </summary>
public record ImageReference(
    string Registry,
    string Repository,
    string Tag,
    string? Digest,
    string FullName
);

/// <summary>
/// Response containing bulk update check results for all projects.
/// </summary>
public record CheckAllUpdatesResponse(
    List<ProjectUpdateSummary> Projects,
    int ProjectsChecked,
    int ProjectsWithUpdates,
    int TotalServicesWithUpdates,
    DateTime CheckedAt
);

// ============================================
// SSE Broadcast Events
// ============================================

/// <summary>
/// SSE event broadcast after completing a bulk update check (periodic or manual).
/// </summary>
public record ProjectUpdatesCheckedEvent(
    List<ProjectUpdateSummary> Projects,
    int ProjectsChecked,
    int ProjectsWithUpdates,
    int TotalServicesWithUpdates,
    DateTime CheckedAt,
    string Trigger  // "periodic" | "manual"
);

/// <summary>
/// SSE event broadcast after checking container updates.
/// </summary>
public record ContainerUpdatesCheckedEvent(
    List<ContainerUpdateSummary> Containers,
    int ContainersChecked,
    int ContainersWithUpdates,
    DateTime CheckedAt
);

/// <summary>
/// Summary of a container's update status.
/// </summary>
public record ContainerUpdateSummary(
    string ContainerId,
    string ContainerName,
    string Image,
    bool UpdateAvailable,
    bool IsComposeManaged,
    string? ProjectName
);

// ============================================
// Pull Progress DTOs (for real-time updates)
// ============================================

/// <summary>
/// Progress information for a single service during pull operation.
/// </summary>
/// <param name="ServiceName">Name of the service being pulled</param>
/// <param name="Status">Current status: waiting, pulling, downloading, extracting, pulled, error</param>
/// <param name="ProgressPercent">Progress percentage (0-100)</param>
/// <param name="Message">Raw log message or status description</param>
public record ServicePullProgress(
    string ServiceName,
    string Status,
    int ProgressPercent,
    string? Message
);

/// <summary>
/// Real-time update progress event sent via SignalR.
/// </summary>
/// <param name="OperationId">Unique operation identifier</param>
/// <param name="ProjectName">Name of the project being updated</param>
/// <param name="Phase">Current phase: pull or recreate</param>
/// <param name="OverallProgress">Overall progress percentage (0-100)</param>
/// <param name="Services">Per-service progress information</param>
/// <param name="CurrentLog">Most recent log line</param>
/// <param name="ContainerId">Container ID for standalone container updates (null for compose project updates)</param>
public record UpdateProgressEvent(
    string OperationId,
    string ProjectName,
    string Phase,
    int OverallProgress,
    List<ServicePullProgress> Services,
    string? CurrentLog,
    string? ContainerId = null
);
