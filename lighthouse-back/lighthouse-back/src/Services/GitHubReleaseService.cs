using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services;

public interface IGitHubReleaseService
{
    Task<AppUpdateCheckResponse> CheckForUpdateAsync(CancellationToken cancellationToken = default);
    Task<List<ReleaseInfo>> GetChangelogBetweenVersionsAsync(string fromVersion, string toVersion, CancellationToken cancellationToken = default);
    Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
    void InvalidateCache();
}

public partial class GitHubReleaseService : IGitHubReleaseService
{
    private readonly HttpClient _httpClient;
    private readonly SelfUpdateOptions _options;
    private readonly IImageDigestService _imageDigestService;
    private readonly IVersionDetectionService _versionDetectionService;
    private readonly ILogger<GitHubReleaseService> _logger;

    private List<GitHubRelease>? _cachedReleases;
    private DateTime _cacheExpiration = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public GitHubReleaseService(
        HttpClient httpClient,
        IOptions<SelfUpdateOptions> options,
        IImageDigestService imageDigestService,
        IVersionDetectionService versionDetectionService,
        ILogger<GitHubReleaseService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _imageDigestService = imageDigestService;
        _versionDetectionService = versionDetectionService;
        _logger = logger;

        // Configure HttpClient for GitHub API
        _httpClient.BaseAddress = new Uri(_options.GitHubApiBaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // Use sync version for UserAgent header (avoid async in constructor)
        string version = _versionDetectionService.GetCurrentVersionSync();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("lighthouse", version));

        if (!string.IsNullOrEmpty(_options.GitHubAccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubAccessToken);
        }
    }

    public async Task<AppUpdateCheckResponse> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use async version detection (includes Docker tag)
            string currentVersion = await _versionDetectionService.GetCurrentVersionAsync();

            // Check if this is a dev version (e.g., "1.2.6-Test-dev-fix")
            if (IsDevVersion(currentVersion))
            {
                return await CheckDevVersionUpdateAsync(currentVersion, cancellationToken);
            }

            // Standard release version update check
            List<GitHubRelease> releases = await GetReleasesAsync(cancellationToken);

            GitHubRelease? latestRelease = releases
                .Where(r => !r.Draft)
                .Where(r => _options.AllowPreRelease || !r.Prerelease)
                .OrderByDescending(r => ParseVersion(r.TagName))
                .FirstOrDefault();

            if (latestRelease == null)
            {
                // Try to find the current version's release for its published date
                GitHubRelease? currentRel = releases
                    .FirstOrDefault(r => NormalizeVersion(r.TagName) == currentVersion);
                DateTime? currentPubAt = currentRel?.PublishedAt ?? currentRel?.CreatedAt;

                return new AppUpdateCheckResponse(
                    CurrentVersion: currentVersion,
                    LatestVersion: currentVersion,
                    UpdateAvailable: false,
                    ReleaseUrl: null,
                    Changelog: new List<ReleaseInfo>(),
                    Summary: new ChangelogSummary(0, false, false, false),
                    CurrentVersionPublishedAt: currentPubAt,
                    LatestVersionPublishedAt: currentPubAt
                );
            }

            string latestVersion = NormalizeVersion(latestRelease.TagName);
            bool updateAvailable = CompareVersions(currentVersion, latestVersion) < 0;

            List<ReleaseInfo> changelog = new();
            ChangelogSummary summary;

            if (updateAvailable)
            {
                changelog = await GetChangelogBetweenVersionsAsync(currentVersion, latestVersion, cancellationToken);
                summary = new ChangelogSummary(
                    TotalReleases: changelog.Count,
                    HasBreakingChanges: changelog.Any(r => r.IsBreakingChange),
                    HasSecurityFixes: changelog.Any(r => r.IsSecurityFix),
                    HasPreReleases: changelog.Any(r => r.IsPreRelease)
                );
            }
            else
            {
                summary = new ChangelogSummary(0, false, false, false);
            }

            // Find the current version's release to get its published date
            GitHubRelease? currentRelease = releases
                .FirstOrDefault(r => NormalizeVersion(r.TagName) == currentVersion);
            DateTime? currentVersionPublishedAt = currentRelease?.PublishedAt ?? currentRelease?.CreatedAt;

            // Get the latest version's published date
            DateTime? latestVersionPublishedAt = latestRelease.PublishedAt ?? latestRelease.CreatedAt;

            return new AppUpdateCheckResponse(
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                UpdateAvailable: updateAvailable,
                ReleaseUrl: latestRelease.HtmlUrl,
                Changelog: changelog,
                Summary: summary,
                CurrentVersionPublishedAt: currentVersionPublishedAt,
                LatestVersionPublishedAt: latestVersionPublishedAt
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for updates from GitHub");
            throw;
        }
    }

    /// <summary>
    /// Determines if a version is a development version (contains branch name suffix).
    /// Examples: "1.2.6-Test-dev-fix" is dev, "1.2.6" is release
    /// </summary>
    private static bool IsDevVersion(string version)
    {
        string normalized = NormalizeVersion(version);
        int dashIndex = normalized.IndexOf('-');
        if (dashIndex <= 0) return false;

        string suffix = normalized.Substring(dashIndex + 1);
        // Dev versions have non-numeric suffixes (branch names)
        // Release pre-releases like "1.0.0-rc.1" are still considered releases
        return !string.IsNullOrEmpty(suffix) &&
               !suffix.StartsWith("rc", StringComparison.OrdinalIgnoreCase) &&
               !suffix.StartsWith("beta", StringComparison.OrdinalIgnoreCase) &&
               !suffix.StartsWith("alpha", StringComparison.OrdinalIgnoreCase) &&
               suffix.Any(c => char.IsLetter(c) && !char.IsDigit(c));
    }

    /// <summary>
    /// Extracts the image tag from the current version for dev versions.
    /// The image tag is the same as the version (e.g., "1.2.6-Test-dev-fix")
    /// </summary>
    private static string GetImageTagFromVersion(string version)
    {
        return NormalizeVersion(version);
    }

    /// <summary>
    /// Checks for updates for dev versions using Docker image digest comparison.
    /// </summary>
    private async Task<AppUpdateCheckResponse> CheckDevVersionUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        string imageTag = GetImageTagFromVersion(currentVersion);
        string fullImageName = $"{_options.DockerImageName}:{imageTag}";

        _logger.LogDebug("Checking dev version update for {Image}", fullImageName);

        try
        {
            // Get host architecture
            string hostArch = await _imageDigestService.GetHostArchitectureAsync(cancellationToken);

            // Get local digest
            ImageDigestInfo localInfo = await _imageDigestService.GetLocalDigestAsync(fullImageName, cancellationToken);

            // Get remote digest
            ImageDigestInfo remoteInfo = await _imageDigestService.GetRemoteDigestAsync(fullImageName, hostArch, cancellationToken);

            // Compare digests
            bool updateAvailable = false;
            if (localInfo.Digest != null && remoteInfo.Digest != null)
            {
                updateAvailable = !localInfo.HasDigest(remoteInfo.Digest);
            }

            _logger.LogDebug(
                "Dev version update check: LocalDigest={LocalDigest}, RemoteDigest={RemoteDigest}, UpdateAvailable={UpdateAvailable}",
                localInfo.Digest ?? "null", remoteInfo.Digest ?? "null", updateAvailable);

            return new AppUpdateCheckResponse(
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion, // Same version, just newer image
                UpdateAvailable: updateAvailable,
                ReleaseUrl: null,
                Changelog: new List<ReleaseInfo>(),
                Summary: new ChangelogSummary(0, false, false, false),
                IsDevVersion: true,
                LocalDigest: localInfo.Digest,
                RemoteDigest: remoteInfo.Digest,
                LocalCreatedAt: localInfo.CreatedAt,
                RemoteCreatedAt: remoteInfo.CreatedAt
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking dev version update for {Version}", currentVersion);
            return new AppUpdateCheckResponse(
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                UpdateAvailable: false,
                ReleaseUrl: null,
                Changelog: new List<ReleaseInfo>(),
                Summary: new ChangelogSummary(0, false, false, false),
                IsDevVersion: true,
                LocalDigest: null,
                RemoteDigest: null,
                LocalCreatedAt: null,
                RemoteCreatedAt: null
            );
        }
    }

    public async Task<List<ReleaseInfo>> GetChangelogBetweenVersionsAsync(string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            List<GitHubRelease> releases = await GetReleasesAsync(cancellationToken);

            Version from = ParseVersion(fromVersion);
            Version to = ParseVersion(toVersion);

            return releases
                .Where(r => !r.Draft)
                .Where(r => _options.AllowPreRelease || !r.Prerelease)
                .Where(r =>
                {
                    Version v = ParseVersion(r.TagName);
                    return v > from && v <= to;
                })
                .OrderByDescending(r => ParseVersion(r.TagName))
                .Select(r => ToReleaseInfo(r))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching changelog between {From} and {To}", fromVersion, toVersion);
            return new List<ReleaseInfo>();
        }
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<GitHubRelease> releases = await GetReleasesAsync(cancellationToken);

            GitHubRelease? latest = releases
                .Where(r => !r.Draft)
                .Where(r => _options.AllowPreRelease || !r.Prerelease)
                .OrderByDescending(r => ParseVersion(r.TagName))
                .FirstOrDefault();

            return latest != null ? ToReleaseInfo(latest) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching latest release");
            return null;
        }
    }

    public void InvalidateCache()
    {
        _cacheExpiration = DateTime.MinValue;
        _cachedReleases = null;
        _logger.LogDebug("GitHub releases cache invalidated");
    }

    private async Task<List<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedReleases != null && DateTime.UtcNow < _cacheExpiration)
            {
                _logger.LogDebug("Using cached releases");
                return _cachedReleases;
            }

            string url = $"/repos/{_options.GitHubRepo}/releases";
            _logger.LogDebug("Fetching releases from GitHub: {Url}", url);

            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("GitHub API returned {StatusCode}: {Content}", response.StatusCode, errorContent);

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // Check for rate limiting
                    if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                        int.TryParse(remaining.FirstOrDefault(), out int rem) && rem == 0)
                    {
                        throw new InvalidOperationException("GitHub API rate limit exceeded. Please try again later or configure a GitHub access token.");
                    }
                }

                throw new HttpRequestException($"GitHub API returned {response.StatusCode}");
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            List<GitHubRelease>? releases = JsonSerializer.Deserialize<List<GitHubRelease>>(content);

            if (releases == null)
            {
                throw new InvalidOperationException("Failed to parse GitHub releases response");
            }

            _cachedReleases = releases;
            _cacheExpiration = DateTime.UtcNow.AddSeconds(_options.CacheDurationSeconds);

            _logger.LogDebug("Fetched {Count} releases from GitHub, cached until {Expiration}", releases.Count, _cacheExpiration);

            return releases;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private ReleaseInfo ToReleaseInfo(GitHubRelease release)
    {
        string notes = release.Body ?? "";
        bool isBreaking = DetectBreakingChange(release);
        bool isSecurity = DetectSecurityFix(release);

        return new ReleaseInfo(
            Version: NormalizeVersion(release.TagName),
            TagName: release.TagName,
            PublishedAt: release.PublishedAt ?? release.CreatedAt,
            ReleaseNotes: notes,
            ReleaseUrl: release.HtmlUrl,
            IsBreakingChange: isBreaking,
            IsSecurityFix: isSecurity,
            IsPreRelease: release.Prerelease
        );
    }

    private bool DetectBreakingChange(GitHubRelease release)
    {
        // Check release name/body for breaking change indicators
        string combined = $"{release.Name} {release.Body}".ToLowerInvariant();
        return combined.Contains("breaking change") ||
               combined.Contains("breaking:") ||
               combined.Contains("major change") ||
               BreakingChangeRegex().IsMatch(release.TagName);
    }

    private bool DetectSecurityFix(GitHubRelease release)
    {
        // Check release name/body for security fix indicators
        string combined = $"{release.Name} {release.Body}".ToLowerInvariant();
        return combined.Contains("security") ||
               combined.Contains("vulnerability") ||
               combined.Contains("cve-");
    }

    private static string NormalizeVersion(string version)
    {
        // Remove 'v' prefix if present
        return version.TrimStart('v', 'V');
    }

    private static Version ParseVersion(string version)
    {
        string normalized = NormalizeVersion(version);

        // Remove pre-release suffix (e.g., -beta.1, -rc.1)
        int dashIndex = normalized.IndexOf('-');
        if (dashIndex > 0)
        {
            normalized = normalized.Substring(0, dashIndex);
        }

        // Try to parse as Version
        if (Version.TryParse(normalized, out Version? parsedVersion))
        {
            return parsedVersion;
        }

        // Default to 0.0.0 if parsing fails
        return new Version(0, 0, 0);
    }

    private static int CompareVersions(string version1, string version2)
    {
        return ParseVersion(version1).CompareTo(ParseVersion(version2));
    }

    // Regex for detecting major version bumps (e.g., v1.0.0 -> v2.0.0)
    [GeneratedRegex(@"^v?(\d+)\.0\.0$")]
    private static partial Regex BreakingChangeRegex();
}

internal class GitHubRelease
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("tarball_url")]
    public string? TarballUrl { get; set; }

    [JsonPropertyName("zipball_url")]
    public string? ZipballUrl { get; set; }
}
