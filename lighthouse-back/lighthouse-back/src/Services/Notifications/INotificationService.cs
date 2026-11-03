namespace Lighthouse.Services.Notifications;

/// <summary>
/// Sends notifications about automatic update events to configured channels
/// (Discord webhook and generic HTTP webhook). Implementations read their configuration from
/// the AppSettings table on each call and silently no-op when disabled or not
/// configured. Notification failures must never propagate to the calling
/// update flow.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Notify that an application self auto-update has been triggered.
    /// The container will restart shortly into the new version.
    /// </summary>
    Task NotifyAppUpdateStartedAsync(string fromVersion, string toVersion, CancellationToken ct = default);

    /// <summary>
    /// Notify that an application self auto-update failed to start.
    /// </summary>
    Task NotifyAppUpdateFailedAsync(string fromVersion, string toVersion, string error, CancellationToken ct = default);

    /// <summary>
    /// Notify that the application has restarted and is now running the new version
    /// (sent from the new instance after a self-update completes).
    /// </summary>
    Task NotifyAppUpdateCompletedAsync(string version, CancellationToken ct = default);

    /// <summary>
    /// Notify the outcome of a compose auto-update cycle (one summary per cycle).
    /// </summary>
    Task NotifyComposeAutoUpdateAsync(ComposeAutoUpdateReport report, CancellationToken ct = default);

    /// <summary>
    /// Notify that an automatic image-prune cycle removed one or more images.
    /// <paramref name="images"/> holds the removed references (repo:tag when
    /// available, otherwise a sha256 id for dangling images).
    /// </summary>
    Task NotifyImagePruneAsync(IReadOnlyList<string> images, long spaceReclaimed, bool danglingOnly, CancellationToken ct = default);
}

/// <summary>
/// Result of a compose auto-update cycle, aggregated per project for notification.
/// </summary>
public class ComposeAutoUpdateReport
{
    public List<ProjectUpdateResult> Projects { get; } = new();

    public int UpdatedCount => Projects.Count(p => p.Success);
    public int FailedCount => Projects.Count(p => !p.Success);
    public bool HasEntries => Projects.Count > 0;
}

/// <summary>
/// Outcome of a single project's auto-update within a cycle.
/// </summary>
public class ProjectUpdateResult
{
    public required string ProjectName { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<ServiceChange> Services { get; init; } = new();
}

/// <summary>
/// A single service's image change (old -> new) within a project update.
/// </summary>
public class ServiceChange
{
    public required string ServiceName { get; init; }
    public required string Image { get; init; }
    public string? OldDigestShort { get; init; }
    public string? NewDigestShort { get; init; }
}

/// <summary>
/// Result of sending a test notification.
/// </summary>
public record NotificationTestResult(bool Success, string? Error);
