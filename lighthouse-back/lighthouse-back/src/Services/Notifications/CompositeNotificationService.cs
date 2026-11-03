namespace Lighthouse.Services.Notifications;

/// <summary>
/// The <see cref="INotificationService"/> callers resolve: fans every notification
/// out to all channels (Discord, generic webhook) concurrently. Each channel is
/// isolated, so one throwing or hanging never skips or delays another, and each is
/// resolved in its own DI scope so concurrent channels never share the
/// (non-thread-safe) scoped AppDbContext.
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<Func<IServiceProvider, INotificationService>> _channelFactories;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IServiceScopeFactory scopeFactory,
        IReadOnlyList<Func<IServiceProvider, INotificationService>> channelFactories,
        ILogger<CompositeNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _channelFactories = channelFactories;
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
        => Task.WhenAll(_channelFactories.Select(factory => SendSafelyAsync(factory, send)));

    private async Task SendSafelyAsync(
        Func<IServiceProvider, INotificationService> factory,
        Func<INotificationService, Task> send)
    {
        string channelName = "unknown";
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            INotificationService channel = factory(scope.ServiceProvider);
            channelName = channel.GetType().Name;
            await send(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification channel {Channel} failed", channelName);
        }
    }
}
