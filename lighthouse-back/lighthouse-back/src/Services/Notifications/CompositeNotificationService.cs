namespace Lighthouse.Services.Notifications;

/// <summary>
/// The <see cref="INotificationService"/> callers resolve: fans every notification
/// out to all channels (Discord, generic webhook) concurrently. Each channel is
/// isolated, so one throwing or hanging never skips or delays another.
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IReadOnlyList<INotificationService> _channels;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IReadOnlyList<INotificationService> channels,
        ILogger<CompositeNotificationService> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public Task NotifyAppUpdateStartedAsync(string fromVersion, string toVersion, CancellationToken ct = default)
        => FanOutAsync(c => c.NotifyAppUpdateStartedAsync(fromVersion, toVersion, ct));

    public Task NotifyAppUpdateFailedAsync(string fromVersion, string toVersion, string error, CancellationToken ct = default)
        => FanOutAsync(c => c.NotifyAppUpdateFailedAsync(fromVersion, toVersion, error, ct));

    public Task NotifyAppUpdateCompletedAsync(string version, CancellationToken ct = default)
        => FanOutAsync(c => c.NotifyAppUpdateCompletedAsync(version, ct));

    public Task NotifyComposeAutoUpdateAsync(ComposeAutoUpdateReport report, CancellationToken ct = default)
        => FanOutAsync(c => c.NotifyComposeAutoUpdateAsync(report, ct));

    public Task NotifyImagePruneAsync(IReadOnlyList<string> images, long spaceReclaimed, bool danglingOnly, CancellationToken ct = default)
        => FanOutAsync(c => c.NotifyImagePruneAsync(images, spaceReclaimed, danglingOnly, ct));

    private Task FanOutAsync(Func<INotificationService, Task> send)
        => Task.WhenAll(_channels.Select(channel => SendSafelyAsync(channel, send)));

    private async Task SendSafelyAsync(INotificationService channel, Func<INotificationService, Task> send)
    {
        try
        {
            await send(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification channel {Channel} failed", channel.GetType().Name);
        }
    }
}
