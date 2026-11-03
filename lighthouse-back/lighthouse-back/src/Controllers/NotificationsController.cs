using Lighthouse.DTOs;
using Lighthouse.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lighthouse.Controllers;

/// <summary>
/// Notification configuration endpoints (Admin only).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class NotificationsController : BaseController
{
    private readonly DiscordNotificationService _discord;
    private readonly WebhookNotificationService _webhook;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        DiscordNotificationService discord,
        WebhookNotificationService webhook,
        ILogger<NotificationsController> logger)
    {
        _discord = discord;
        _webhook = webhook;
        _logger = logger;
    }

    /// <summary>
    /// Send a test notification. If a webhook URL is supplied in the body it is
    /// tested (lets the user verify before saving); otherwise the saved one is used.
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<object>>> SendTest(
        [FromBody] TestNotificationRequest? request, CancellationToken ct)
    {
        string? overrideUrl = request?.WebhookUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl) && !DiscordWebhookUrl.IsValid(overrideUrl))
        {
            return BadRequest(ApiResponse.Fail<object>("Invalid Discord webhook URL"));
        }

        NotificationTestResult result = await _discord.SendTestAsync(overrideUrl, ct);
        if (result.Success)
        {
            return Ok(ApiResponse.Ok<object>(null, "Test notification sent"));
        }

        _logger.LogInformation("Test notification failed: {Error}", result.Error);
        return BadRequest(ApiResponse.Fail<object>(result.Error ?? "Failed to send test notification"));
    }

    /// <summary>
    /// Send a test notification to the generic webhook. A supplied URL is tested
    /// instead of the saved one; headers null = saved headers, "" = no headers.
    /// </summary>
    [HttpPost("test/webhook")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<object>>> SendWebhookTest(
        [FromBody] TestWebhookRequest? request, CancellationToken ct)
    {
        string? overrideUrl = request?.Url;
        if (!string.IsNullOrWhiteSpace(overrideUrl) && !WebhookUrl.IsValid(overrideUrl))
        {
            return BadRequest(ApiResponse.Fail<object>("Invalid webhook URL"));
        }

        string? overrideHeaders = request?.Headers;
        if (overrideHeaders != null && !WebhookHeaders.TryParse(overrideHeaders, out _, out string? headerError))
        {
            return BadRequest(ApiResponse.Fail<object>(headerError ?? "Invalid headers"));
        }

        NotificationTestResult result = await _webhook.SendTestAsync(overrideUrl, overrideHeaders, ct);
        if (result.Success)
        {
            return Ok(ApiResponse.Ok<object>(null, "Test notification sent"));
        }

        _logger.LogInformation("Webhook test notification failed: {Error}", result.Error);
        return BadRequest(ApiResponse.Fail<object>(result.Error ?? "Failed to send test notification"));
    }
}

/// <summary>Optional override webhook URL for the test endpoint.</summary>
public record TestNotificationRequest(string? WebhookUrl = null);

/// <summary>Optional override URL/headers for the generic webhook test endpoint.</summary>
public record TestWebhookRequest(string? Url = null, string? Headers = null);
