using System.Net;
using System.Net.Http.Headers;
using Lighthouse.Configuration;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services.Registry;

/// <summary>
/// Registry client for GitHub Container Registry (ghcr.io).
/// </summary>
/// <remarks>
/// GHCR always answers manifest requests with a 401 Bearer challenge, even for public images. The
/// credentials (from the Docker config, i.e. <c>docker login ghcr.io</c>, or a configured GitHub
/// token) are sent as <c>Basic</c> auth to the challenge's token endpoint, which then issues a
/// registry token tied to the account rather than an anonymous one.
/// </remarks>
public class GhcrRegistryClient : RegistryClientBase
{
    private readonly IConfiguration _configuration;
    private readonly IRegistryCredentialService _credentialService;

    private const string RegistryHost = "ghcr.io";
    private const string RegistryUrl = "https://ghcr.io/v2";

    /// <summary>GHCR's token endpoint accepts any username alongside a personal access token.</summary>
    private const string TokenOnlyUsername = "token";

    public GhcrRegistryClient(
        HttpClient httpClient,
        IOptions<UpdateCheckOptions> options,
        IConfiguration configuration,
        IRegistryCredentialService credentialService,
        ILogger<GhcrRegistryClient> logger)
        : base(httpClient, logger, options.Value.TimeoutSeconds)
    {
        _configuration = configuration;
        _credentialService = credentialService;
    }

    public override bool CanHandle(string registry)
    {
        return registry == "ghcr.io";
    }

    public override async Task<string?> GetManifestDigestAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        // The registry parameter is ignored: this client always talks to ghcr.io.
        try
        {
            return await FetchManifestDigestViaHeadAsync(repository, tag, architecture, cancellationToken);
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest (HEAD) for ghcr.io/{Repository}:{Tag}", repository, tag);
            return null;
        }
    }

    /// <summary>
    /// Resolves the manifest digest via HEAD (not counted against pull-rate limits), handling the
    /// 401 token challenge. Falls back to GET when HEAD is unsupported or omits the digest.
    /// </summary>
    private async Task<string?> FetchManifestDigestViaHeadAsync(
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken)
    {
        string url = $"{RegistryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Head, url);
        AddManifestAcceptHeaders(request);

        HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw RegistryRateLimitException.FromResponse(response);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            string? newToken = await HandleUnauthorizedAsync(response, repository, cancellationToken);
            if (newToken == null)
            {
                return null;
            }
            return await FetchManifestDigestViaHeadWithTokenAsync(repository, tag, architecture, newToken, cancellationToken);
        }

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            var (fallbackDigest, _) = await GetManifestDigestAndCreatedAtAsync("ghcr.io", repository, tag, architecture, cancellationToken);
            return fallbackDigest;
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest HEAD request failed for ghcr.io/{Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return null;
        }

        if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
        {
            return digestValues.FirstOrDefault();
        }

        var (digest, _) = await GetManifestDigestAndCreatedAtAsync("ghcr.io", repository, tag, architecture, cancellationToken);
        return digest;
    }

    private async Task<string?> FetchManifestDigestViaHeadWithTokenAsync(
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
            throw RegistryRateLimitException.FromResponse(response);
        }

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            var (fallbackDigest, _) = await GetManifestDigestAndCreatedAtAsync("ghcr.io", repository, tag, architecture, cancellationToken);
            return fallbackDigest;
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest HEAD (token) request failed for ghcr.io/{Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return null;
        }

        if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
        {
            return digestValues.FirstOrDefault();
        }

        var (digest, _) = await GetManifestDigestAndCreatedAtAsync("ghcr.io", repository, tag, architecture, cancellationToken);
        return digest;
    }

    public override async Task<(string? Digest, DateTime? CreatedAt)> GetManifestDigestAndCreatedAtAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        // The registry parameter is ignored: this client always talks to ghcr.io.
        try
        {
            return await FetchManifestDigestAndCreatedAtAsync(repository, tag, architecture, cancellationToken);
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest for ghcr.io/{Repository}:{Tag}", repository, tag);
            return (null, null);
        }
    }

    private async Task<(string? Digest, DateTime? CreatedAt)> FetchManifestDigestAndCreatedAtAsync(
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken)
    {
        string url = $"{RegistryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        AddManifestAcceptHeaders(request);

        HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw RegistryRateLimitException.FromResponse(response);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            string? newToken = await HandleUnauthorizedAsync(response, repository, cancellationToken);
            if (newToken != null)
            {
                return await FetchManifestDigestAndCreatedAtWithTokenAsync(repository, tag, architecture, newToken, cancellationToken);
            }
            return (null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest request failed for ghcr.io/{Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return (null, null);
        }

        return await ExtractDigestAndCreatedAtFromResponseAsync(response, repository, architecture, token: null, cancellationToken);
    }

    private async Task<(string? Digest, DateTime? CreatedAt)> FetchManifestDigestAndCreatedAtWithTokenAsync(
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
            throw RegistryRateLimitException.FromResponse(response);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Manifest request with token failed for ghcr.io/{Repository}:{Tag} with status {StatusCode}",
                repository, tag, response.StatusCode);
            return (null, null);
        }

        return await ExtractDigestAndCreatedAtFromResponseAsync(response, repository, architecture, token, cancellationToken);
    }

    private async Task<(string? Digest, DateTime? CreatedAt)> ExtractDigestAndCreatedAtFromResponseAsync(
        HttpResponseMessage response,
        string repository,
        string architecture,
        string? token,
        CancellationToken cancellationToken)
    {
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        string content = await response.Content.ReadAsStringAsync(cancellationToken);

        // The Docker-Content-Digest header is what Docker stores locally: the manifest-list digest for
        // multi-arch images, or the manifest digest for single-arch images.
        string? digest = null;
        if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
        {
            digest = digestValues.FirstOrDefault();
        }

        if (contentType.Contains("manifest.list") || contentType.Contains("image.index"))
        {
            // Use the manifest-list digest (header) for comparison; fetch the per-arch manifest to get
            // the config blob for the creation date.
            string? archManifestDigest = ExtractDigestFromManifestList(content, architecture);
            if (archManifestDigest == null)
            {
                return (digest, null);
            }

            string? configDigest = await FetchConfigDigestFromManifestAsync(RegistryUrl, repository, archManifestDigest, token, cancellationToken);
            DateTime? createdAt = null;
            if (configDigest != null)
            {
                createdAt = await FetchConfigCreatedAtAsync(RegistryUrl, repository, configDigest, token, cancellationToken);
            }
            return (digest, createdAt);
        }

        // Single manifest - extract config digest from manifest body
        string? singleConfigDigest = ExtractConfigDigestFromManifest(content);
        DateTime? singleCreatedAt = null;
        if (singleConfigDigest != null)
        {
            singleCreatedAt = await FetchConfigCreatedAtAsync(RegistryUrl, repository, singleConfigDigest, token, cancellationToken);
        }

        return (digest, singleCreatedAt);
    }

    /// <summary>
    /// Resolves the <c>Basic</c> credentials sent to GHCR's token endpoint: the <c>ghcr.io</c> entry of
    /// the Docker config first (the same credentials <c>docker compose pull</c> uses), then a GitHub
    /// token from configuration (<c>GitHub__Token</c> / <c>SelfUpdate__GitHubAccessToken</c>).
    /// Returns <c>null</c> when none is configured (anonymous token).
    /// </summary>
    private async Task<AuthenticationHeaderValue?> GetBasicCredentialsAsync()
    {
        (string Username, string Password)? credentials = await _credentialService.GetRawCredentialsAsync(RegistryHost);
        if (credentials.HasValue)
        {
            Logger.LogDebug("Using Docker config credentials for ghcr.io (user: {User})", credentials.Value.Username);
            return CreateBasicAuthHeader(credentials.Value.Username, credentials.Value.Password);
        }

        string? gitHubToken = _configuration["GitHub:Token"] ?? _configuration["SelfUpdate:GitHubAccessToken"];
        if (!string.IsNullOrEmpty(gitHubToken))
        {
            Logger.LogDebug("Using configured GitHub token for ghcr.io");
            return CreateBasicAuthHeader(TokenOnlyUsername, gitHubToken);
        }

        Logger.LogDebug("No ghcr.io credentials configured; using an anonymous token (shared per-IP limits apply)");
        return null;
    }

    private async Task<string?> HandleUnauthorizedAsync(
        HttpResponseMessage response,
        string repository,
        CancellationToken cancellationToken)
    {
        // Parse WWW-Authenticate header to get token endpoint
        if (!response.Headers.TryGetValues("WWW-Authenticate", out IEnumerable<string>? authHeaders))
        {
            return null;
        }

        string? authHeader = authHeaders.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer "))
        {
            return null;
        }

        var parameters = ParseBearerParameters(authHeader);

        if (!parameters.TryGetValue("realm", out string? realm) ||
            !parameters.TryGetValue("scope", out string? scope))
        {
            return null;
        }

        parameters.TryGetValue("service", out string? service);

        string tokenUrl = $"{realm}?scope={Uri.EscapeDataString(scope)}";
        if (!string.IsNullOrEmpty(service))
        {
            tokenUrl += $"&service={Uri.EscapeDataString(service)}";
        }

        try
        {
            // Only hand the GitHub credentials to GHCR's own token endpoint.
            AuthenticationHeaderValue? basicAuth = IsGhcrRealm(realm) ? await GetBasicCredentialsAsync() : null;
            return await RequestBearerTokenAsync(tokenUrl, basicAuth, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to get GHCR token");
        }

        return null;
    }

    private static bool IsGhcrRealm(string realm)
        => Uri.TryCreate(realm, UriKind.Absolute, out Uri? uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals(RegistryHost, StringComparison.OrdinalIgnoreCase);
}
