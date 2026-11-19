namespace Lighthouse.Models;

public class Operation
{
    public int Id { get; set; }
    public string OperationId { get; set; } = Guid.NewGuid().ToString();
    public int? UserId { get; set; }
    public required string Type { get; set; } // up, down, pull, build, etc.
    public required string Status { get; set; } // pending, running, completed, failed, cancelled
    public int Progress { get; set; } = 0; // 0-100
    public string? ProjectPath { get; set; }
    public string? ProjectName { get; set; }
    public string? Logs { get; set; } // Accumulated logs
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public bool IsAcknowledged { get; set; } = false;

    // Navigation property
    public User? User { get; set; }
}

/// <summary>
/// Operation type constants
/// </summary>
public static class OperationType
{
    public const string ComposeUp = "compose_up";
    public const string ComposeDown = "compose_down";
    public const string ComposeBuild = "compose_build";
    public const string ComposePull = "compose_pull";
    public const string ComposeRestart = "compose_restart";
    public const string ComposeStart = "compose_start";
    public const string ComposeStop = "compose_stop";
    public const string ComposeRerun = "compose_rerun";
    public const string ContainerStart = "container_start";
    public const string ContainerStop = "container_stop";
    public const string ContainerRestart = "container_restart";
    public const string ContainerRemove = "container_remove";
    public const string ComposeUpdate = "compose_update";
    public const string ContainerUpdate = "container_update";
}

/// <summary>
/// Operation status constants
/// </summary>
public static class OperationStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
