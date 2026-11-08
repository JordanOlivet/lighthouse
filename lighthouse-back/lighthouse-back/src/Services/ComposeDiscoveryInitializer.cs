using Microsoft.Extensions.Hosting;

namespace Lighthouse.Services;

/// <summary>
/// Hosted service that warms the compose file cache on application startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/> returns immediately; the scan runs in the background so startup is
/// never blocked (a cold scan can take tens of seconds when disks are in standby).
/// </para>
/// <para>
/// Requests arriving before the scan completes join the same in-flight scan instead of starting
/// their own (see <see cref="ComposeFileCacheService"/>). Errors are logged and never prevent
/// startup; the next cache read retries the scan.
/// </para>
/// </remarks>
public class ComposeDiscoveryInitializer : IHostedService
{
    private readonly IComposeFileCacheService _cacheService;
    private readonly ILogger<ComposeDiscoveryInitializer> _logger;

    public ComposeDiscoveryInitializer(
        IComposeFileCacheService cacheService,
        ILogger<ComposeDiscoveryInitializer> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the initial compose files scan in the background and returns immediately.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Starting initial compose files scan...");
                List<Models.DiscoveredComposeFile> files = await _cacheService.GetOrScanAsync();
                _logger.LogDebug("Initial compose files scan completed. Found {Count} files.", files.Count);
            }
            catch (Exception ex)
            {
                // Don't throw - the app must start even if the scan fails; the next read retries.
                _logger.LogError(ex, "Error during initial compose files scan");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("ComposeDiscoveryInitializer stopping");
        return Task.CompletedTask;
    }
}
