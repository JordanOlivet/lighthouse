using Docker.DotNet.Models;

namespace Lighthouse.Utils;

/// <summary>
/// Run status of a one-shot container (init / copy / migration task that is expected to
/// exit). Serialized as its name in <c>OneShotStatus</c> fields.
/// </summary>
public enum OneShotRunStatus
{
    /// <summary>Created but never started.</summary>
    Pending,
    /// <summary>Currently executing its task.</summary>
    Running,
    /// <summary>Exited with code 0: the task succeeded.</summary>
    Completed,
    /// <summary>Exited with a non-zero code (or died): the task failed.</summary>
    Failed,
}

/// <summary>
/// A <c>depends_on</c> entry as recorded by Compose v2 in the
/// <c>com.docker.compose.depends_on</c> label of the dependent container.
/// </summary>
public sealed record ComposeDependsOnEntry(string Service, string Condition, bool Restart);

/// <summary>
/// Facts read from a container inspect that the one-shot detection needs.
/// </summary>
/// <param name="RestartPolicy">Restart policy name (<c>no</c>, <c>always</c>, ...), null when unset.</param>
/// <param name="ExitCode">Last exit code.</param>
/// <param name="FinishedAt">When the last run finished (UTC), null if it never finished.</param>
public sealed record OneShotInspectData(string? RestartPolicy, long? ExitCode, DateTime? FinishedAt);

/// <summary>
/// Resolved one-shot information for a container.
/// </summary>
public sealed record OneShotInfo(OneShotRunStatus Status, long? ExitCode, DateTime? FinishedAt);

/// <summary>
/// Detects "one-shot" compose services: services that run a task and exit (code 0 meaning
/// success) instead of running continuously.
/// </summary>
/// <remarks>
/// Detection rules, in priority order:
/// <list type="number">
/// <item>Explicit label <c>lighthouse.oneshot=true</c> on the service: always a one-shot.
/// <c>lighthouse.oneshot=false</c> disables detection entirely for that service.</item>
/// <item>Heuristic: another service of the same project depends on it with
/// <c>condition: service_completed_successfully</c>, and its restart policy is <c>no</c>
/// (or unset). The restart policy is only available from a container inspect, so the
/// heuristic is resolved in two steps: <see cref="FindCandidates"/> on the container list,
/// then <see cref="IsOneShot"/> with the inspected restart policy.</item>
/// </list>
/// </remarks>
public static class OneShotDetector
{
    public const string OneShotLabel = "lighthouse.oneshot";
    public const string DependsOnLabel = "com.docker.compose.depends_on";
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";
    public const string CompletedSuccessfullyCondition = "service_completed_successfully";

    /// <summary>Reads a container label, null when the labels or the key are missing.</summary>
    public static string? GetLabel(IDictionary<string, string>? labels, string key) =>
        labels != null && labels.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Parses the <c>com.docker.compose.depends_on</c> label. Format (Compose v2):
    /// comma-separated <c>service:condition:restart</c> entries, e.g.
    /// <c>src:service_completed_successfully:true,db:service_started:false</c>.
    /// Older Compose versions omit the <c>restart</c> part; it then defaults to false.
    /// </summary>
    public static IReadOnlyList<ComposeDependsOnEntry> ParseDependsOn(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return Array.Empty<ComposeDependsOnEntry>();

        List<ComposeDependsOnEntry> entries = new();
        foreach (string rawEntry in label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = rawEntry.Split(':');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                continue;

            string condition = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            bool restart = parts.Length > 2 && bool.TryParse(parts[2].Trim(), out bool r) && r;
            entries.Add(new ComposeDependsOnEntry(parts[0].Trim(), condition, restart));
        }

        return entries;
    }

    /// <summary>
    /// Reads the explicit <c>lighthouse.oneshot</c> label: true / false, or null when absent
    /// or not a boolean.
    /// </summary>
    public static bool? ReadOneShotLabel(IDictionary<string, string>? labels)
    {
        if (labels == null || !labels.TryGetValue(OneShotLabel, out string? value))
            return null;

        return value?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Finds the containers that may be one-shots. Returns container ID → whether the
    /// container is explicitly labelled (<c>true</c>: one-shot regardless of restart policy;
    /// <c>false</c>: heuristic candidate whose restart policy still has to be checked with
    /// <see cref="IsOneShot"/>).
    /// </summary>
    public static Dictionary<string, bool> FindCandidates(IEnumerable<ContainerListResponse> containers)
    {
        List<ContainerListResponse> list = containers.Where(c => c.Labels != null).ToList();

        // Services that a sibling waits on with service_completed_successfully, per project.
        HashSet<(string Project, string Service)> awaitedServices = new();
        foreach (ContainerListResponse container in list)
        {
            string project = GetLabel(container.Labels, ComposeProjectLabel) ?? string.Empty;
            foreach (ComposeDependsOnEntry entry in ParseDependsOn(GetLabel(container.Labels, DependsOnLabel)))
            {
                if (string.Equals(entry.Condition, CompletedSuccessfullyCondition, StringComparison.OrdinalIgnoreCase))
                    awaitedServices.Add((project, entry.Service));
            }
        }

        Dictionary<string, bool> candidates = new(StringComparer.Ordinal);
        foreach (ContainerListResponse container in list)
        {
            bool? label = ReadOneShotLabel(container.Labels);
            if (label == false)
                continue;

            if (label == true)
            {
                candidates[container.ID] = true;
                continue;
            }

            string? project = GetLabel(container.Labels, ComposeProjectLabel);
            string? service = GetLabel(container.Labels, ComposeServiceLabel);
            if (project != null && service != null && awaitedServices.Contains((project, service)))
                candidates[container.ID] = false;
        }

        return candidates;
    }

    /// <summary>
    /// Resolves the one-shot containers of <paramref name="containers"/>: finds the candidates,
    /// inspects only those (restart policy, exit code, finish time) and applies
    /// <see cref="IsOneShot"/>. Returns container ID → one-shot info; non one-shots are absent.
    /// A failed inspect falls back to the list data (exit code parsed from the status string)
    /// for explicitly labelled containers and drops heuristic candidates.
    /// </summary>
    public static async Task<Dictionary<string, OneShotInfo>> ResolveAsync(
        IEnumerable<ContainerListResponse> containers,
        Func<string, Task<OneShotInspectData?>> inspect)
    {
        List<ContainerListResponse> list = containers.ToList();
        Dictionary<string, bool> candidates = FindCandidates(list);
        Dictionary<string, OneShotInfo> result = new(StringComparer.Ordinal);
        if (candidates.Count == 0)
            return result;

        Dictionary<string, ContainerListResponse> byId = list
            .Where(c => c.ID != null)
            .GroupBy(c => c.ID)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        (string Id, OneShotInspectData? Data)[] inspected = await Task.WhenAll(candidates.Keys.Select(async id =>
        {
            try { return (id, await inspect(id)); }
            catch { return (id, (OneShotInspectData?)null); }
        }));

        foreach ((string id, OneShotInspectData? data) in inspected)
        {
            bool explicitLabel = candidates[id];
            if (data == null && !explicitLabel)
                continue;
            if (data != null && !IsOneShot(explicitLabel, data.RestartPolicy))
                continue;
            if (!byId.TryGetValue(id, out ContainerListResponse? container))
                continue;

            long? exitCode = data?.ExitCode ?? ParseExitCodeFromStatus(container.Status);
            OneShotRunStatus status = GetRunStatus(container.State, exitCode);
            DateTime? finishedAt = status is OneShotRunStatus.Completed or OneShotRunStatus.Failed
                ? data?.FinishedAt
                : null;
            result[id] = new OneShotInfo(status, exitCode, finishedAt);
        }

        return result;
    }

    /// <summary>
    /// Final one-shot decision for a candidate: an explicit label always wins; a heuristic
    /// candidate must have a <c>no</c> (or unset) restart policy, since a service that Docker
    /// restarts on exit is not a one-shot.
    /// </summary>
    /// <param name="explicitLabel">Value from <see cref="FindCandidates"/>.</param>
    /// <param name="restartPolicy">Restart policy name from the container inspect
    /// (<c>no</c>, <c>always</c>, <c>on-failure</c>, <c>unless-stopped</c>), null/empty when unset.</param>
    public static bool IsOneShot(bool explicitLabel, string? restartPolicy) =>
        explicitLabel || string.IsNullOrEmpty(restartPolicy) || string.Equals(restartPolicy, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a one-shot container's Docker state and exit code to its run status.
    /// </summary>
    /// <param name="dockerState">Docker state (<c>running</c>, <c>exited</c>, <c>created</c>, ...), case-insensitive.</param>
    /// <param name="exitCode">Last exit code, null when unknown.</param>
    public static OneShotRunStatus GetRunStatus(string? dockerState, long? exitCode) =>
        dockerState?.Trim().ToLowerInvariant() switch
        {
            "running" or "restarting" or "paused" => OneShotRunStatus.Running,
            "created" => OneShotRunStatus.Pending,
            "exited" or "stopped" => exitCode == 0 ? OneShotRunStatus.Completed : OneShotRunStatus.Failed,
            _ => OneShotRunStatus.Failed, // dead, removing, unknown
        };

    /// <summary>
    /// Whether a one-shot in this status is "idle as expected" and must not count as a
    /// stopped / failed container in counters and project state.
    /// </summary>
    public static bool IsIdleAsExpected(OneShotRunStatus status) =>
        status is OneShotRunStatus.Completed or OneShotRunStatus.Pending;

    /// <summary>
    /// Parses the exit code from a Docker list status string such as
    /// <c>Exited (3) 4 seconds ago</c>. Returns null when absent.
    /// </summary>
    public static long? ParseExitCodeFromStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;

        int open = status.IndexOf('(');
        int close = open >= 0 ? status.IndexOf(')', open + 1) : -1;
        if (!status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase) || open < 0 || close < 0)
            return null;

        return long.TryParse(status.AsSpan(open + 1, close - open - 1), out long code) ? code : null;
    }

    /// <summary>
    /// Compose services to pass to <c>up -d --force-recreate</c> to re-run
    /// <paramref name="serviceName"/>: the one-shot itself followed by its running
    /// <c>restart: true</c> dependents. Returns null when the service is not a one-shot of
    /// <paramref name="projectName"/>.
    /// </summary>
    /// <param name="containers">Containers of the host (or of the project).</param>
    /// <param name="oneShots">Resolved one-shots, from <see cref="ResolveAsync"/>.</param>
    public static List<string>? GetRerunTargets(
        IEnumerable<ContainerListResponse> containers,
        IReadOnlyDictionary<string, OneShotInfo> oneShots,
        string projectName,
        string serviceName)
    {
        List<ContainerListResponse> projectContainers = containers
            .Where(c => string.Equals(GetLabel(c.Labels, ComposeProjectLabel), projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool isOneShot = projectContainers.Any(c =>
            GetLabel(c.Labels, ComposeServiceLabel) == serviceName && c.ID != null && oneShots.ContainsKey(c.ID));
        if (!isOneShot)
            return null;

        Dictionary<string, bool> runStates = ContainerRunStateHelper.GetComposeServiceRunStates(projectContainers, projectName);
        return new[] { serviceName }
            .Concat(GetRestartDependents(projectContainers, projectName, serviceName)
                .Where(d => runStates.TryGetValue(d, out bool running) && running))
            .ToList();
    }

    /// <summary>
    /// Services of <paramref name="projectName"/> that depend on <paramref name="oneShotService"/>
    /// with <c>restart: true</c>. Compose only restarts those dependents when they are part of
    /// the same <c>up</c> invocation as the one-shot (verified with Compose v5: a targeted
    /// <c>up --force-recreate &lt;one-shot&gt;</c> leaves them untouched), so callers add them to
    /// the target list.
    /// </summary>
    public static List<string> GetRestartDependents(
        IEnumerable<ContainerListResponse> containers,
        string projectName,
        string oneShotService)
    {
        HashSet<string> dependents = new(StringComparer.Ordinal);
        foreach (ContainerListResponse container in containers)
        {
            if (container.Labels == null
                || !string.Equals(GetLabel(container.Labels, ComposeProjectLabel), projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            string? service = GetLabel(container.Labels, ComposeServiceLabel);
            if (string.IsNullOrEmpty(service) || service == oneShotService)
                continue;

            if (ParseDependsOn(GetLabel(container.Labels, DependsOnLabel))
                .Any(e => e.Service == oneShotService && e.Restart))
                dependents.Add(service);
        }

        return dependents.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
