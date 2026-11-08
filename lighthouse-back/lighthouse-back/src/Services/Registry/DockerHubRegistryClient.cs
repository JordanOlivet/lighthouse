using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lighthouse.Configuration;
using Lighthouse.Services;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services.Registry;

/// <summary>
/// Registry client for Docker Hub (docker.io / registry.hub.docker.com).
/// </summary>
public class DockerHubRegistryClient : RegistryClientBase
{
    private readonly IRegistryCredentialService _credentialService;

    private const string AuthUrl = "https://auth.docker.io/token";
    private const string RegistryUrl = "https://registry-1.docker.io/v2";
    private const string DockerHubRegistryKey = "https://index.docker.io/v1/";

    public DockerHubRegistryClient(
        HttpClient httpClient,
        IOptions<UpdateCheckOptions> options,
        IRegistryCredentialService credentialService,
        ILogger<DockerHubRegistryClient> logger)
        : base(httpClient, logger, options.Value.TimeoutSeconds)
    {
        _credentialService = credentialService;
    }

    public override bool CanHandle(string registry)
    {
        return registry == "docker.io" ||
               registry == "registry-1.docker.io" ||
               registry == "registry.hub.docker.com" ||
               registry == "index.docker.io";
    }

    public override async Task<string?> GetManifestDigestAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        // The registry parameter is ignored: this client always talks to registry-1.docker.io.
        try
        {
            string? token = await GetAuthTokenAsync(repository, cancellationToken);
            if (token == null)
            {
                Logger.LogWarning("Failed to get auth token for repository {Repository}", repository);
                return null;
            }

            return await FetchManifestDigestViaHeadAsync(repository, tag, architecture, token, cancellationToken);
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest (HEAD) for {Repository}:{Tag}", repository, tag);
            return null;
        }
    }

    /// <summary>
    /// Resolves the manifest digest with a HEAD request. Docker Hub does not count HEAD manifest
    /// requests against the pull-rate limit, so this is used for the periodic "did it change" check.
    /// Falls back to GET when the registry rejects HEAD or omits the digest header.
    /// </summary>
    private async Task<string?> FetchManifestDigestViaHeadAsync(
        string repository,
        string tag,
        string architecture,
        string token,
        CancellationToken cancellationToken)
    {
        string url = $"{RegistryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Head, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddManifestAcceptHeaders(request);

        HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            LogRateLimitHeaders(response, repository, tag, "HEAD");
            throw RegistryRateLimitException.FromResponse(response);
        }

        // Some registries don't allow HEAD on manifests → fall back to the GET path.
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            Logger.LogDebug("HEAD not supported for {Repository}:{Tag} ({StatusCode}); falling back to GET",
                repository, tag, response.StatusCode);
            var (fallbackDigest, _) = await FetchManifestDigestAndConfigAsync(repository, tag, architecture, token, cancellationToken);
            return fallbackDigest;
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest HEAD request failed for {Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return null;
        }

        // HEAD returns the rate-limit headers without consuming a pull → ideal place to log the tier.
        LogRateLimitHeaders(response, repository, tag, "HEAD");

        if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
        {
            return digestValues.FirstOrDefault();
        }

        // No digest header (unexpected) → fall back to GET.
        var (digest, _) = await FetchManifestDigestAndConfigAsync(repository, tag, architecture, token, cancellationToken);
        return digest;
    }

    public override async Task<(string? Digest, DateTime? CreatedAt)> GetManifestDigestAndCreatedAtAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        // The registry parameter is ignored: this client always talks to registry-1.docker.io.
        try
        {
            string? token = await GetAuthTokenAsync(repository, cancellationToken);
            if (token == null)
            {
                Logger.LogWarning("Failed to get auth token for repository {Repository}", repository);
                return (null, null);
            }

            var (digest, configDigest) = await FetchManifestDigestAndConfigAsync(repository, tag, architecture, token, cancellationToken);

            if (digest == null)
            {
                return (null, null);
            }

            DateTime? createdAt = null;
            if (configDigest != null)
            {
                createdAt = await FetchConfigCreatedAtAsync(RegistryUrl, repository, configDigest, token, cancellationToken);
            }

            return (digest, createdAt);
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest for {Repository}:{Tag}", repository, tag);
            return (null, null);
        }
    }

    private async Task<string?> GetAuthTokenAsync(string repository, CancellationToken cancellationToken)
    {
        try
        {
            // Docker Hub uses a token service for authentication
            string scope = $"repository:{repository}:pull";
            string url = $"{AuthUrl}?service=registry.docker.io&scope={Uri.EscapeDataString(scope)}";

            using HttpRequestMessage tokenRequest = new(HttpMethod.Get, url);

            // Use configured Docker Hub credentials if available to bypass anonymous rate limits
            var credentials = await _credentialService.GetRawCredentialsAsync(DockerHubRegistryKey);
            if (credentials.HasValue)
            {
                tokenRequest.Headers.Authorization =
                    CreateBasicAuthHeader(credentials.Value.Username, credentials.Value.Password);
                Logger.LogDebug("Using authenticated Docker Hub token request for {Repository} (user: {User})",
                    repository, credentials.Value.Username);
            }
            else
            {
                Logger.LogDebug(
                    "No Docker Hub credentials resolved for {Repository}; using anonymous token (guest rate limits apply)",
                    repository);
            }

            HttpResponseMessage response = await HttpClient.SendAsync(tokenRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw RegistryRateLimitException.FromResponse(response);
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Auth token request failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("token", out JsonElement tokenElement))
            {
                return tokenElement.GetString();
            }

            return null;
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get Docker Hub auth token for {Repository}", repository);
            return null;
        }
    }

    private async Task<(string? Digest, string? ConfigDigest)> FetchManifestDigestAndConfigAsync(
        string repository,
        string tag,
        string architecture,
        string token,
        CancellationToken cancellationToken)
    {
        string url = $"{RegistryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddManifestAcceptHeaders(request);

        HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            LogRateLimitHeaders(response, repository, tag, "GET");
            throw RegistryRateLimitException.FromResponse(response);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest request failed for {Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return (null, null);
        }

        LogRateLimitHeaders(response, repository, tag, "GET");

        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        string content = await response.Content.ReadAsStringAsync(cancellationToken);

        // Get the digest from Docker-Content-Digest header - this is what Docker stores locally.
        // For multi-arch images this is the manifest list digest; for single-arch the manifest digest.
        string? digest = null;
        if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
        {
            digest = digestValues.FirstOrDefault();
        }

        if (contentType.Contains("manifest.list") || contentType.Contains("image.index"))
        {
            // For multi-arch we use the manifest list digest (from header) for comparison, but fetch
            // the architecture-specific manifest to get the config digest for the creation date.
            string? archManifestDigest = ExtractDigestFromManifestList(content, architecture);
            if (archManifestDigest == null)
            {
                return (digest, null);
            }

            string? configDigest = await FetchConfigDigestFromManifestAsync(RegistryUrl, repository, archManifestDigest, token, cancellationToken);
            return (digest, configDigest);
        }

        // Single manifest - extract config digest from manifest body
        string? singleConfigDigest = ExtractConfigDigestFromManifest(content);

        return (digest, singleConfigDigest);
    }

    // Throttle for the low-remaining warning, shared across concurrent checks (UtcTicks; 0 = never).
    private static long _lastLowRemainingWarnTicks;
    private const int LowRemainingThreshold = 10;
    private static readonly TimeSpan LowRemainingWarnInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Logs Docker Hub's rate-limit headers at Debug so the authenticated tier can be confirmed
    /// (limit 100 = anonymous, 200 = authenticated free, absent = unlimited/Pro). Emits a single
    /// throttled Warning when the remaining budget runs low. Header values use the "N;w=window" form.
    /// </summary>
    private void LogRateLimitHeaders(HttpResponseMessage response, string repository, string tag, string phase)
    {
        string? limit = FirstHeader(response, "ratelimit-limit");
        string? remaining = FirstHeader(response, "ratelimit-remaining");
        string? source = FirstHeader(response, "docker-ratelimit-source");

        if (limit == null && remaining == null)
        {
            // No rate-limit headers → either a Pro/Team (unlimited) account or the registry didn't report.
            Logger.LogDebug(
                "Docker Hub rate limit for {Repository}:{Tag} ({Phase}): no rate-limit headers (unlimited/Pro or not reported)",
                repository, tag, phase);
            return;
        }

        int? limitValue = ParseLeadingInt(limit);
        string tier = limitValue switch
        {
            null => "unknown",
            <= 100 => "anonymous (guest)",
            _ => "authenticated"
        };

        Logger.LogDebug(
            "Docker Hub rate limit for {Repository}:{Tag} ({Phase}): tier={Tier}, limit={Limit}, remaining={Remaining}, source={Source}",
            repository, tag, phase, tier, limit ?? "n/a", remaining ?? "n/a", source ?? "n/a");

        int? remainingValue = ParseLeadingInt(remaining);
        if (remainingValue is { } rem && rem < LowRemainingThreshold)
        {
            WarnLowRemainingThrottled(rem, limitValue, tier);
        }
    }

    private void WarnLowRemainingThrottled(int remaining, int? limit, string tier)
    {
        long now = DateTimeOffset.UtcNow.UtcTicks;
        long last = Interlocked.Read(ref _lastLowRemainingWarnTicks);
        if (last != 0 && new DateTimeOffset(now, TimeSpan.Zero) - new DateTimeOffset(last, TimeSpan.Zero) < LowRemainingWarnInterval)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _lastLowRemainingWarnTicks, now, last) != last)
        {
            return; // another thread already warned
        }

        Logger.LogWarning(
            "Docker Hub pull quota nearly exhausted: {Remaining} of {Limit} remaining ({Tier}). " +
            "Authenticate with a Docker Hub account in Registry Management (or raise the check interval) to avoid 429s.",
            remaining, limit?.ToString() ?? "?", tier);
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    /// <summary>Parses the leading integer from a header value like "198;w=21600" → 198.</summary>
    private static int? ParseLeadingInt(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        int semicolon = headerValue.IndexOf(';');
        string number = (semicolon >= 0 ? headerValue[..semicolon] : headerValue).Trim();
        return int.TryParse(number, out int value) ? value : null;
    }
}
