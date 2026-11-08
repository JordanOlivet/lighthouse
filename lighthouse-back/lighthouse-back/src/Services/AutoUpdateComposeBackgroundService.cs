using Cronos;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services;

/// <summary>
/// Background service that runs compose project auto-updates on a cron schedule.
/// Disabled by default. Skips projects flagged with x-auto-update: false.
/// Defers cycles when a self-update is in progress and re-runs immediately
/// on the next application start via the PendingComposeAutoUpdate flag.
/// </summary>
public class AutoUpdateComposeBackgroundService : BackgroundService
{
    public const string EnabledKey = "AutoUpdateComposeEnabled";
    public const string CronKey = "AutoUpdateComposeCron";
    public const string PendingKey = "PendingComposeAutoUpdate";
    public const string DefaultCron = "0 2 * * *";

    // Short fixed tick: every tick we check whether a cron occurrence fell within the
    // elapsed window. This never misses the scheduled minute and picks up enable/cron
    // changes within one tick (unlike a blind Task.Delay until the next occurrence).
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _serviceProvider;
    private readonly UpdateCheckOptions _options;
    private readonly ILogger<AutoUpdateComposeBackgroundService> _logger;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    public AutoUpdateComposeBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<UpdateCheckOptions> options,
        ILogger<AutoUpdateComposeBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoUpdateComposeBackgroundService starting");

        // On startup, consume the pending flag (previous app self-update preempted a compose cycle)
        await ConsumePendingAutoUpdateFlagAsync(stoppingToken);

        // Window anchor: we fire when a cron occurrence falls in (lastCheckUtc, nowUtc].
        DateTime lastCheckUtc = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);

                (bool enabled, string cron) = await ReadSettingsAsync(stoppingToken);
                DateTime nowUtc = DateTime.UtcNow;

                if (!enabled)
                {
                    // Advance the window so occurrences during the disabled period don't fire later
                    lastCheckUtc = nowUtc;
                    continue;
                }

                if (!TryParseCron(cron, out CronExpression? expression))
                {
                    _logger.LogWarning("Invalid cron expression '{Cron}' for AutoUpdateCompose.", cron);
                    lastCheckUtc = nowUtc;
                    continue;
                }

                DateTime? occurrence = expression!.GetNextOccurrence(lastCheckUtc, inclusive: false);
                if (occurrence.HasValue && occurrence.Value <= nowUtc)
                {
                    _logger.LogInformation("AutoUpdateCompose: scheduled occurrence {Occurrence:O} is due, running cycle", occurrence.Value);
                    await RunCycleAsync("scheduled", stoppingToken);
                }

                lastCheckUtc = nowUtc;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AutoUpdateComposeBackgroundService loop");
            }
        }

        _logger.LogInformation("AutoUpdateComposeBackgroundService stopped");
    }

    /// <summary>Runs one auto-update cycle. Internal for tests.</summary>
    internal async Task RunCycleAsync(string trigger, CancellationToken ct)
    {
        if (!await _cycleLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("Auto-update compose cycle already in progress, skipping");
            return;
        }

        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();

            ISelfUpdateService selfUpdateService = scope.ServiceProvider.GetRequiredService<ISelfUpdateService>();
            if (selfUpdateService.IsUpdateInProgress)
            {
                _logger.LogInformation("Self-update in progress, deferring compose auto-update cycle");
                await SetPendingAutoUpdateFlagAsync(scope, true, ct);
                return;
            }

            IComposeUpdateService composeUpdateService = scope.ServiceProvider.GetRequiredService<IComposeUpdateService>();
            IComposeFileCacheService fileCacheService = scope.ServiceProvider.GetRequiredService<IComposeFileCacheService>();
            INotificationService notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var report = new ComposeAutoUpdateReport();

            _logger.LogInformation("AutoUpdateCompose cycle started (trigger: {Trigger})", trigger);

            // Use cached check results (forceRefresh: false) to avoid hammering registries
            // with ~N manifest requests every cycle. ProjectUpdateCheckBackgroundService keeps
            // the cache warm; uncached projects are still checked on demand. This sharply cuts
            // Docker Hub anonymous-rate-limit pressure during the bulk update that follows.
            DTOs.CheckAllUpdatesResponse checkResult = await composeUpdateService.CheckAllProjectsUpdatesAsync(userId: 1, forceRefresh: false, ct);

            List<DiscoveredComposeFile> files = await fileCacheService.GetOrScanAsync();
            Dictionary<string, DiscoveredComposeFile> filesByProject = files
                .GroupBy(f => f.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int updated = 0;
            int skippedFlag = 0;
            bool anyUpdateAttempted = false;
            TimeSpan projectDelay = TimeSpan.FromSeconds(Math.Max(0, _options.AutoUpdateProjectDelaySeconds));
            TimeSpan rateLimitRetryDelay = TimeSpan.FromSeconds(Math.Max(0, _options.AutoUpdateRateLimitRetryDelaySeconds));
            bool deferRateLimited = _options.AutoUpdateRateLimitRetryDelaySeconds > 0;
            var deferred = new List<(string ProjectName, List<ServiceChange> Changes)>();

            foreach (DTOs.ProjectUpdateSummary summary in checkResult.Projects)
            {
                if (ct.IsCancellationRequested) break;
                if (summary.ServicesWithUpdates <= 0) continue;

                if (filesByProject.TryGetValue(summary.ProjectName, out DiscoveredComposeFile? file)
                    && !file.AutoUpdateEnabled)
                {
                    skippedFlag++;
                    _logger.LogInformation("Skipping auto-update for project {Project}: x-auto-update is false", summary.ProjectName);
                    continue;
                }

                // Space pulls out across projects so a burst of `docker compose pull` calls does not
                // trip per-minute registry rate limits (ghcr.io in particular). First update runs
                // immediately; subsequent ones wait the configured delay.
                if (anyUpdateAttempted && projectDelay > TimeSpan.Zero)
                {
                    await Task.Delay(projectDelay, ct);
                }
                anyUpdateAttempted = true;

                // Capture per-service old/new digests for the notification before updating
                // (cached check; ProjectUpdateCheckBackgroundService keeps the cache warm).
                List<ServiceChange> serviceChanges = await CaptureServiceChangesAsync(composeUpdateService, summary.ProjectName, ct);

                _logger.LogInformation("Auto-updating project {Project} ({Count} services with updates)",
                    summary.ProjectName, summary.ServicesWithUpdates);

                (ProjectUpdateResult result, bool rateLimited) =
                    await UpdateOneProjectAsync(composeUpdateService, summary.ProjectName, serviceChanges, ct);

                if (!result.Success && rateLimited && deferRateLimited)
                {
                    // Registry throttling (ghcr.io in particular) usually outlasts the in-pull retry
                    // window but clears within a few minutes: retry once at the end of the cycle
                    // instead of reporting a failure and waiting for the next night.
                    _logger.LogWarning(
                        "Pull for project {Project} still rate limited; deferring it to the end of the cycle",
                        summary.ProjectName);
                    deferred.Add((summary.ProjectName, serviceChanges));
                    continue;
                }

                report.Projects.Add(result);
                if (result.Success) updated++;
            }

            if (deferred.Count > 0 && !ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Retrying {Count} rate-limited project(s) in {Delay:0}s: {Projects}",
                    deferred.Count, rateLimitRetryDelay.TotalSeconds, string.Join(", ", deferred.Select(d => d.ProjectName)));
                await Task.Delay(rateLimitRetryDelay, ct);

                bool first = true;
                foreach ((string projectName, List<ServiceChange> changes) in deferred)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!first && projectDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(projectDelay, ct);
                    }
                    first = false;

                    _logger.LogInformation("Retrying deferred auto-update for project {Project}", projectName);
                    (ProjectUpdateResult result, _) = await UpdateOneProjectAsync(composeUpdateService, projectName, changes, ct);
                    report.Projects.Add(result);
                    if (result.Success) updated++;
                }
            }

            _logger.LogInformation(
                "AutoUpdateCompose cycle complete: {Updated} updated, {SkippedFlag} skipped via x-auto-update, {Deferred} retried after rate limit, {Total} projects checked",
                updated, skippedFlag, deferred.Count, checkResult.ProjectsChecked);

            // One summary notification per cycle (only when something was attempted).
            if (report.HasEntries)
            {
                await notificationService.NotifyComposeAutoUpdateAsync(report, ct);
            }
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// Updates one project (pull + recreate) and maps the outcome to a report entry. Never throws
    /// (except on cancellation); <c>RateLimited</c> is true when the pull failed on registry 429s.
    /// </summary>
    private async Task<(ProjectUpdateResult Result, bool RateLimited)> UpdateOneProjectAsync(
        IComposeUpdateService composeUpdateService,
        string projectName,
        List<ServiceChange> serviceChanges,
        CancellationToken ct)
    {
        try
        {
            DTOs.UpdateTriggerResponse response = await composeUpdateService.UpdateProjectAsync(
                projectName: projectName,
                services: null,
                updateAll: true,
                restartFullProject: true,
                restartAfterUpdate: true,
                userId: 1,
                ipAddress: "system",
                ct: ct);

            if (response.Success)
            {
                _logger.LogInformation("Auto-update completed for project {Project} (operation: {OperationId})",
                    projectName, response.OperationId);
            }
            else
            {
                _logger.LogWarning("Auto-update returned non-success for project {Project}: {Message}",
                    projectName, response.Message);
            }

            var result = new ProjectUpdateResult
            {
                ProjectName = projectName,
                Success = response.Success,
                Error = response.Success ? null : response.Message,
                Services = serviceChanges
            };
            return (result, !response.Success && response.RateLimited);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-updating project {Project}", projectName);
            var result = new ProjectUpdateResult
            {
                ProjectName = projectName,
                Success = false,
                Error = ex.Message,
                Services = serviceChanges
            };
            return (result, false);
        }
    }

    /// <summary>
    /// Reads the cached per-service update check for a project and maps services
    /// with an available update into <see cref="ServiceChange"/> (image + short
    /// old/new digests) for the notification. Best-effort: returns an empty list on error.
    /// </summary>
    private async Task<List<ServiceChange>> CaptureServiceChangesAsync(
        IComposeUpdateService composeUpdateService, string projectName, CancellationToken ct)
    {
        try
        {
            DTOs.ProjectUpdateCheckResponse check = await composeUpdateService.CheckProjectUpdatesAsync(projectName, forceRefresh: false, ct);
            return check.Images
                .Where(i => i.UpdateAvailable && i.UpdatePolicy != "disabled")
                .Select(i => new ServiceChange
                {
                    ServiceName = i.ServiceName,
                    Image = i.Image,
                    OldDigestShort = ShortDigest(i.LocalDigest),
                    NewDigestShort = ShortDigest(i.RemoteDigest)
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture service changes for {Project}", projectName);
            return new List<ServiceChange>();
        }
    }

    /// <summary>Trims a digest to a short form (drops "sha256:" prefix, keeps 12 chars).</summary>
    private static string? ShortDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        int colon = digest.IndexOf(':');
        string hex = colon >= 0 ? digest[(colon + 1)..] : digest;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    private async Task ConsumePendingAutoUpdateFlagAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AppSetting? pending = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == PendingKey, ct);
            bool isPending = pending != null
                && bool.TryParse(pending.Value, out bool b)
                && b;

            if (!isPending) return;

            _logger.LogInformation("PendingComposeAutoUpdate flag detected, running an immediate compose auto-update cycle");

            if (pending != null)
            {
                pending.Value = "false";
                await db.SaveChangesAsync(ct);
            }

            await RunCycleAsync("pending-after-restart", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read/consume PendingComposeAutoUpdate flag");
        }
    }

    private async Task<(bool Enabled, string Cron)> ReadSettingsAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            AppSetting? enabledSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == EnabledKey, ct);
            AppSetting? cronSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == CronKey, ct);

            bool enabled = enabledSetting != null
                && bool.TryParse(enabledSetting.Value, out bool b)
                && b;

            string cron = !string.IsNullOrWhiteSpace(cronSetting?.Value) ? cronSetting!.Value : DefaultCron;
            return (enabled, cron);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read AutoUpdateCompose settings; treating as disabled");
            return (false, DefaultCron);
        }
    }

    private static async Task SetPendingAutoUpdateFlagAsync(IServiceScope scope, bool value, CancellationToken ct)
    {
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AppSetting? setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == PendingKey, ct);
        if (setting == null)
        {
            setting = new AppSetting { Key = PendingKey, Value = value ? "true" : "false" };
            db.AppSettings.Add(setting);
        }
        else
        {
            setting.Value = value ? "true" : "false";
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool TryParseCron(string expression, out CronExpression? parsed)
    {
        try
        {
            CronFormat format = expression.Trim().Split(' ').Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
            parsed = CronExpression.Parse(expression, format);
            return true;
        }
        catch
        {
            parsed = null;
            return false;
        }
    }
}
