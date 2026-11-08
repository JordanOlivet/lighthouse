namespace Lighthouse.Configuration;

/// <summary>
/// Configuration options for compose project update checking functionality.
/// </summary>
public class UpdateCheckOptions
{
    public const string SectionName = "UpdateCheck";

    /// <summary>
    /// Whether update checking is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fallback cache lifetime in minutes for update check results, used only before the effective
    /// check interval has been published. Normally the cache tracks the check interval.
    /// </summary>
    public int CacheDurationMinutes { get; set; } = 720;

    /// <summary>
    /// Maximum number of concurrent image update checks.
    /// </summary>
    public int MaxConcurrentChecks { get; set; } = 5;

    /// <summary>
    /// Timeout in seconds for registry API calls.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retry attempts for failed registry API calls.
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// List of project names to exclude from update checking.
    /// </summary>
    public List<string> ExcludedProjects { get; set; } = new();

    /// <summary>
    /// List of image patterns to exclude from update checking.
    /// Supports wildcards (*) for pattern matching.
    /// </summary>
    public List<string> ExcludedImages { get; set; } = new();

    /// <summary>
    /// Interval in minutes between automatic update checks. Defaults to 12h to keep registry load low.
    /// Can be overridden via AppSettings key "ProjectUpdateCheckIntervalMinutes".
    /// </summary>
    public int CheckIntervalMinutes { get; set; } = 720;

    /// <summary>
    /// Delay in seconds before the first automatic update check after startup.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Number of extra attempts for a `docker compose pull` that fails with a registry rate limit
    /// (HTTP 429 / toomanyrequests). The first attempt is not counted, so a value of 4 means up to
    /// 5 total attempts. ghcr.io meters per minute (its reported sub-millisecond Retry-After is
    /// bogus), so the backoff is sized to span a full minute and let the per-minute bucket reset.
    /// </summary>
    public int PullRetryAttempts { get; set; } = 4;

    /// <summary>
    /// Base/floor delay in seconds for the exponential backoff between rate-limited pull retries.
    /// Used instead of the (often bogus, sub-millisecond) Retry-After reported by the registry.
    /// </summary>
    public int PullRetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Maximum delay in seconds for a single pull-retry backoff, and the cap on how long the pull
    /// path will wait for an active rate-limit cooldown before proceeding anyway. Sized so the total
    /// retry window crosses ghcr.io's per-minute rate-limit reset.
    /// </summary>
    public int PullRetryMaxDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Random jitter applied to each rate-limited pull-retry backoff, as a fraction of the backoff
    /// (0.2 = ±20%). Spreads retries so concurrent/sequential projects don't re-burst the registry
    /// in lockstep. Clamped to [0, 1).
    /// </summary>
    public double PullRetryJitterFactor { get; set; } = 0.2;

    /// <summary>
    /// Delay in seconds inserted between projects during an automatic compose update cycle, to
    /// spread registry pulls out and avoid bursting past per-minute rate limits.
    /// </summary>
    public int AutoUpdateProjectDelaySeconds { get; set; } = 3;

    /// <summary>
    /// When a project's pull is still rate limited after the pull retries, the automatic compose
    /// update cycle defers it to the end of the cycle and retries it once after waiting this many
    /// seconds (counted from the end of the regular pass). ghcr.io throttling observed in production
    /// typically clears within 1-4 minutes, longer than the in-pull retry window. 0 disables the
    /// deferred retry (the project is reported as failed immediately, as before).
    /// </summary>
    public int AutoUpdateRateLimitRetryDelaySeconds { get; set; } = 300;
}
