using System.Text;
using System.Text.Json;
using Lighthouse.Data;
using Lighthouse.Models;
using Microsoft.EntityFrameworkCore;

namespace Lighthouse.Services.Notifications;

/// <summary>
/// Sends update notifications to a Discord channel via an incoming webhook.
/// Configuration (enabled flag + webhook URL) is stored in the AppSettings table
/// and read on each call so changes take effect without a restart. All sends are
/// best-effort: failures are logged and swallowed so they never break an update.
/// </summary>
public class DiscordNotificationService : INotificationService
{
    public const string EnabledKey = "NotificationsDiscordEnabled";
    public const string WebhookUrlKey = "NotificationsDiscordWebhookUrl";

    // Discord embed colors (decimal RGB)
    private const int ColorSuccess = 0x57F287; // green
    private const int ColorFailure = 0xED4245; // red
    private const int ColorInfo = 0x5865F2;    // blurple
    private const int ColorNeutral = 0x95A5A6; // gray

    // Discord embed limits
    private const int MaxFieldValueLength = 1024;
    private const int MaxFields = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _db;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(
        HttpClient httpClient,
        AppDbContext db,
        ILogger<DiscordNotificationService> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _logger = logger;
    }

    public Task NotifyAppUpdateStartedAsync(string fromVersion, string toVersion, CancellationToken ct = default)
    {
        var embed = new DiscordEmbed
        {
            Title = "🚀 Application update started",
            Description = $"Updating from **{fromVersion}** to **{toVersion}**. The application will restart shortly.",
            Color = ColorInfo,
            Timestamp = DateTime.UtcNow
        };
        return SendEmbedAsync(embed, ct);
    }

    public Task NotifyAppUpdateFailedAsync(string fromVersion, string toVersion, string error, CancellationToken ct = default)
    {
        var embed = new DiscordEmbed
        {
            Title = "❌ Application update failed",
            Description = $"Could not start the update from **{fromVersion}** to **{toVersion}**.",
            Color = ColorFailure,
            Timestamp = DateTime.UtcNow,
            Fields = { new DiscordField("Error", NotificationFormatting.Truncate(error, MaxFieldValueLength), false) }
        };
        return SendEmbedAsync(embed, ct);
    }

    public Task NotifyAppUpdateCompletedAsync(string version, CancellationToken ct = default)
    {
        var embed = new DiscordEmbed
        {
            Title = "✅ Application updated",
            Description = $"The application restarted and is now running **{version}**.",
            Color = ColorSuccess,
            Timestamp = DateTime.UtcNow
        };
        return SendEmbedAsync(embed, ct);
    }

    public Task NotifyComposeAutoUpdateAsync(ComposeAutoUpdateReport report, CancellationToken ct = default)
    {
        if (report == null || !report.HasEntries)
        {
            return Task.CompletedTask;
        }

        bool anyFailure = report.FailedCount > 0;
        var embed = new DiscordEmbed
        {
            Title = $"🐳 Compose auto-update — {report.UpdatedCount} updated, {report.FailedCount} failed",
            Description = report.UpdatedCount > 0 || report.FailedCount > 0
                ? $"Auto-update cycle completed at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC."
                : null,
            Color = anyFailure ? (report.UpdatedCount > 0 ? ColorNeutral : ColorFailure) : ColorSuccess,
            Timestamp = DateTime.UtcNow
        };

        foreach (ProjectUpdateResult project in report.Projects.Take(MaxFields))
        {
            string statusIcon = project.Success ? "✅" : "❌";
            var sb = new StringBuilder();

            foreach (ServiceChange svc in project.Services)
            {
                string change = svc.OldDigestShort != null && svc.NewDigestShort != null
                    ? $"`{svc.OldDigestShort}` → `{svc.NewDigestShort}`"
                    : "updated";
                sb.AppendLine($"• **{svc.ServiceName}** ({svc.Image}): {change}");
            }

            if (!project.Success && !string.IsNullOrWhiteSpace(project.Error))
            {
                sb.AppendLine($"⚠️ {project.Error}");
            }

            if (sb.Length == 0)
            {
                sb.AppendLine(project.Success ? "Updated." : "Failed.");
            }

            embed.Fields.Add(new DiscordField(
                $"{statusIcon} {project.ProjectName}",
                NotificationFormatting.Truncate(sb.ToString().TrimEnd(), MaxFieldValueLength),
                false));
        }

        return SendEmbedAsync(embed, ct);
    }

    public Task NotifyImagePruneAsync(IReadOnlyList<string> images, long spaceReclaimed, bool danglingOnly, CancellationToken ct = default)
    {
        if (images == null || images.Count == 0)
        {
            return Task.CompletedTask;
        }

        var embed = new DiscordEmbed
        {
            Title = $"🧹 Image auto-prune — {images.Count} removed",
            Description = $"Reclaimed **{NotificationFormatting.FormatBytes(spaceReclaimed)}** · {(danglingOnly ? "dangling images only" : "all unused images")}.",
            Color = ColorSuccess,
            Timestamp = DateTime.UtcNow
        };

        const int maxLines = 20;
        var sb = new StringBuilder();
        foreach (string entry in images.Take(maxLines))
        {
            sb.AppendLine($"• `{NotificationFormatting.FormatImageRef(entry)}`");
        }
        if (images.Count > maxLines)
        {
            sb.AppendLine($"… +{images.Count - maxLines} more");
        }
        embed.Fields.Add(new DiscordField("Removed", NotificationFormatting.Truncate(sb.ToString().TrimEnd(), MaxFieldValueLength), false));

        return SendEmbedAsync(embed, ct);
    }

    public async Task<NotificationTestResult> SendTestAsync(string? overrideWebhookUrl, CancellationToken ct = default)
    {
        string? webhookUrl = !string.IsNullOrWhiteSpace(overrideWebhookUrl)
            ? overrideWebhookUrl
            : await GetSettingAsync(WebhookUrlKey, ct);

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new NotificationTestResult(false, "No webhook URL configured.");
        }

        var embed = new DiscordEmbed
        {
            Title = "🔔 Test notification",
            Description = "Discord notifications are configured correctly for Lighthouse.",
            Color = ColorInfo,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            await PostEmbedAsync(webhookUrl, embed, ct);
            return new NotificationTestResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord test notification failed");
            return new NotificationTestResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Sends an embed using the saved configuration. No-ops (with a debug log)
    /// when notifications are disabled or the webhook URL is missing, and never
    /// throws.
    /// </summary>
    private async Task SendEmbedAsync(DiscordEmbed embed, CancellationToken ct)
    {
        try
        {
            if (!await IsEnabledAsync(ct))
            {
                _logger.LogDebug("Discord notifications disabled; skipping '{Title}'", embed.Title);
                return;
            }

            string? webhookUrl = await GetSettingAsync(WebhookUrlKey, ct);
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                _logger.LogDebug("Discord notifications enabled but no webhook URL set; skipping '{Title}'", embed.Title);
                return;
            }

            await PostEmbedAsync(webhookUrl, embed, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: never let a notification failure break an update flow.
            _logger.LogWarning(ex, "Failed to send Discord notification '{Title}'", embed.Title);
        }
    }

    private async Task PostEmbedAsync(string webhookUrl, DiscordEmbed embed, CancellationToken ct)
    {
        var payload = new { embeds = new[] { embed } };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _httpClient.PostAsync(webhookUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Discord webhook returned {(int)response.StatusCode}: {NotificationFormatting.Truncate(body, 200)}");
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
}

/// <summary>Discord embed object serialized into the webhook payload.</summary>
internal class DiscordEmbed
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Color { get; set; }
    public DateTime? Timestamp { get; set; }
    public List<DiscordField> Fields { get; set; } = new();
}

/// <summary>A single embed field (name/value, optional inline).</summary>
internal record DiscordField(string Name, string Value, bool Inline);
