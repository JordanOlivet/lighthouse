using System.Text;
using System.Text.Json;
using Lighthouse.Data;
using Lighthouse.Models;
using Microsoft.EntityFrameworkCore;

namespace Lighthouse.Services.Notifications;

/// <summary>
/// Sends update notifications as JSON to a generic HTTP webhook. The payload is
/// Apprise-compatible (<c>title</c>/<c>body</c>/<c>type</c>) and also carries
/// structured <c>event</c>/<c>data</c> fields for other consumers. Configuration
/// (enabled flag, URL, free-form headers) is stored in the AppSettings table and
/// read on each call. All sends are best-effort: failures are logged and swallowed
/// so they never break an update. Header values are never logged.
/// </summary>
public class WebhookNotificationService : INotificationService
{
    public const string EnabledKey = "NotificationsWebhookEnabled";
    public const string UrlKey = "NotificationsWebhookUrl";
    public const string HeadersKey = "NotificationsWebhookHeaders";

    private const string TypeInfo = "info";
    private const string TypeSuccess = "success";
    private const string TypeWarning = "warning";
    private const string TypeFailure = "failure";

    private const int MaxPruneBodyLines = 20;

    // Nulls are kept so consumers always see the same shape.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _db;
    private readonly ILogger<WebhookNotificationService> _logger;

    public WebhookNotificationService(
        HttpClient httpClient,
        AppDbContext db,
        ILogger<WebhookNotificationService> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _logger = logger;
    }

    public Task NotifyAppUpdateStartedAsync(string fromVersion, string toVersion, CancellationToken ct = default)
    {
        return SendAsync(new WebhookPayload(
            "🚀 Application update started",
            $"Updating from {fromVersion} to {toVersion}. The application will restart shortly.",
            TypeInfo,
            "app.update.started",
            DateTime.UtcNow,
            new { fromVersion, toVersion }), ct);
    }

    public Task NotifyAppUpdateFailedAsync(string fromVersion, string toVersion, string error, CancellationToken ct = default)
    {
        return SendAsync(new WebhookPayload(
            "❌ Application update failed",
            $"Could not start the update from {fromVersion} to {toVersion}.\nError: {error}",
            TypeFailure,
            "app.update.failed",
            DateTime.UtcNow,
            new { fromVersion, toVersion, error }), ct);
    }

    public Task NotifyAppUpdateCompletedAsync(string version, CancellationToken ct = default)
    {
        return SendAsync(new WebhookPayload(
            "✅ Application updated",
            $"The application restarted and is now running {version}.",
            TypeSuccess,
            "app.update.completed",
            DateTime.UtcNow,
            new { version }), ct);
    }

    public Task NotifyComposeAutoUpdateAsync(ComposeAutoUpdateReport report, CancellationToken ct = default)
    {
        if (report == null || !report.HasEntries)
        {
            return Task.CompletedTask;
        }

        string type = report.FailedCount == 0
            ? TypeSuccess
            : report.UpdatedCount > 0 ? TypeWarning : TypeFailure;

        var body = new StringBuilder();
        foreach (ProjectUpdateResult project in report.Projects)
        {
            body.AppendLine($"{(project.Success ? "✅" : "❌")} {project.ProjectName}");
            int linesBefore = body.Length;

            foreach (ServiceChange svc in project.Services)
            {
                string change = svc.OldDigestShort != null && svc.NewDigestShort != null
                    ? $"{svc.OldDigestShort} → {svc.NewDigestShort}"
                    : "updated";
                body.AppendLine($"  • {svc.ServiceName} ({svc.Image}): {change}");
            }

            if (!project.Success && !string.IsNullOrWhiteSpace(project.Error))
            {
                body.AppendLine($"  ⚠ {project.Error}");
            }

            if (body.Length == linesBefore)
            {
                body.AppendLine(project.Success ? "  Updated." : "  Failed.");
            }
        }

        var data = new
        {
            updatedCount = report.UpdatedCount,
            failedCount = report.FailedCount,
            projects = report.Projects.Select(p => new
            {
                name = p.ProjectName,
                success = p.Success,
                error = p.Error,
                services = p.Services.Select(s => new
                {
                    name = s.ServiceName,
                    image = s.Image,
                    oldDigest = s.OldDigestShort,
                    newDigest = s.NewDigestShort
                })
            })
        };

        return SendAsync(new WebhookPayload(
            $"🐳 Compose auto-update — {report.UpdatedCount} updated, {report.FailedCount} failed",
            body.ToString().TrimEnd(),
            type,
            "compose.autoupdate",
            DateTime.UtcNow,
            data), ct);
    }

    public Task NotifyImagePruneAsync(IReadOnlyList<string> images, long spaceReclaimed, bool danglingOnly, CancellationToken ct = default)
    {
        if (images == null || images.Count == 0)
        {
            return Task.CompletedTask;
        }

        var body = new StringBuilder();
        body.AppendLine($"Reclaimed {NotificationFormatting.FormatBytes(spaceReclaimed)} · {(danglingOnly ? "dangling images only" : "all unused images")}.");
        foreach (string entry in images.Take(MaxPruneBodyLines))
        {
            body.AppendLine($"  • {NotificationFormatting.FormatImageRef(entry)}");
        }
        if (images.Count > MaxPruneBodyLines)
        {
            body.AppendLine($"… +{images.Count - MaxPruneBodyLines} more");
        }

        return SendAsync(new WebhookPayload(
            $"🧹 Image auto-prune — {images.Count} removed",
            body.ToString().TrimEnd(),
            TypeSuccess,
            "images.prune",
            DateTime.UtcNow,
            new { images, count = images.Count, spaceReclaimedBytes = spaceReclaimed, danglingOnly }), ct);
    }

    /// <summary>
    /// Sends a test notification, even when the channel is disabled.
    /// <paramref name="overrideUrl"/> null/blank uses the saved URL;
    /// <paramref name="overrideHeaders"/> null uses the saved headers, "" sends none.
    /// </summary>
    public async Task<NotificationTestResult> SendTestAsync(string? overrideUrl, string? overrideHeaders, CancellationToken ct = default)
    {
        string? url = !string.IsNullOrWhiteSpace(overrideUrl)
            ? overrideUrl
            : await GetSettingAsync(UrlKey, ct);

        if (string.IsNullOrWhiteSpace(url))
        {
            return new NotificationTestResult(false, "No webhook URL configured.");
        }

        string? headersText = overrideHeaders ?? await GetSettingAsync(HeadersKey, ct);
        if (!WebhookHeaders.TryParse(headersText, out IReadOnlyList<KeyValuePair<string, string>> headers, out string? headerError))
        {
            return new NotificationTestResult(false, $"Invalid headers: {headerError}");
        }

        var payload = new WebhookPayload(
            "🔔 Test notification",
            "Webhook notifications are configured correctly for Lighthouse.",
            TypeInfo,
            "test",
            DateTime.UtcNow,
            new { });

        try
        {
            await PostAsync(url, headers, payload, ct);
            return new NotificationTestResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook test notification failed");
            return new NotificationTestResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Sends a payload using the saved configuration. No-ops (with a debug log)
    /// when disabled or the URL is missing, and never throws.
    /// </summary>
    private async Task SendAsync(WebhookPayload payload, CancellationToken ct)
    {
        try
        {
            if (!await IsEnabledAsync(ct))
            {
                _logger.LogDebug("Webhook notifications disabled; skipping '{Event}'", payload.Event);
                return;
            }

            string? url = await GetSettingAsync(UrlKey, ct);
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogDebug("Webhook notifications enabled but no URL set; skipping '{Event}'", payload.Event);
                return;
            }

            if (!WebhookHeaders.TryParse(await GetSettingAsync(HeadersKey, ct),
                    out IReadOnlyList<KeyValuePair<string, string>> headers, out string? headerError))
            {
                _logger.LogWarning("Saved webhook headers are invalid ({Error}); skipping '{Event}'", headerError, payload.Event);
                return;
            }

            await PostAsync(url, headers, payload, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: never let a notification failure break an update flow.
            _logger.LogWarning(ex, "Failed to send webhook notification '{Event}'", payload.Event);
        }
    }

    private async Task PostAsync(
        string url,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        WebhookPayload payload,
        CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        foreach ((string name, string value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Webhook returned {(int)response.StatusCode}: {NotificationFormatting.Truncate(body, 200)}");
        }
    }

    private async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        string? value = await GetSettingAsync(EnabledKey, ct);
        return bool.TryParse(value, out bool enabled) && enabled;
    }

    private async Task<string?> GetSettingAsync(string key, CancellationToken ct)
    {
        AppSetting? setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting?.Value;
    }

    /// <summary>JSON body sent to the webhook (camelCase on the wire).</summary>
    private sealed record WebhookPayload(
        string Title,
        string Body,
        string Type,
        string Event,
        DateTime Timestamp,
        object Data);
}
