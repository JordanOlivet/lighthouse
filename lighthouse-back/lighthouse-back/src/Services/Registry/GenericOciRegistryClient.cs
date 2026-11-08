using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lighthouse.Configuration;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services.Registry;

/// <summary>
/// Generic registry client for OCI-compliant registries.
/// Used as a fallback for registries without specific implementations (e.g., lscr.io, mcr.microsoft.com).
/// </summary>
/// <remarks>
/// When the Docker config holds credentials for the registry host (<c>docker login &lt;host&gt;</c>),
/// they are sent as <c>Basic</c> auth to the challenge's token endpoint — the same behaviour as the
/// Docker daemon — so checks count against the account rather than the anonymous quota.
/// </remarks>
public class GenericOciRegistryClient : RegistryClientBase
{
    private readonly IRegistryCredentialService _credentialService;

    public GenericOciRegistryClient(
        HttpClient httpClient,
        IOptions<UpdateCheckOptions> options,
        IRegistryCredentialService credentialService,
        ILogger<GenericOciRegistryClient> logger)
        : base(httpClient, logger, options.Value.TimeoutSeconds)
    {
        _credentialService = credentialService;
    }

    public override bool CanHandle(string registry)
    {
        // This is the fallback client, so it can handle any registry
        return true;
    }

    public override async Task<string?> GetManifestDigestAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string registryUrl = $"https://{registry}/v2";

            // Try HEAD anonymously first (HEAD is not counted against pull-rate limits).
            string? digest = await TryFetchDigestViaHeadAsync(
                registryUrl, repository, tag, architecture, null, cancellationToken);
            if (digest != null)
            {
                return digest;
            }

            string? token = await GetTokenViaWwwAuthenticateAsync(registry, registryUrl, repository, tag, cancellationToken);
            if (token != null)
            {
                return await TryFetchDigestViaHeadAsync(
                    registryUrl, repository, tag, architecture, token, cancellationToken);
            }

            return null;
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest (HEAD) for {Registry}/{Repository}:{Tag}", registry, repository, tag);
            return null;
        }
    }

    /// <summary>
    /// Resolves the manifest digest with a HEAD request. Falls back to the GET path when the registry
    /// rejects HEAD or omits the digest header. Throws <see cref="RegistryRateLimitException"/> on 429.
    /// </summary>
    private async Task<string?> TryFetchDigestViaHeadAsync(
        string registryUrl,
        string repository,
        string tag,
        string architecture,
        string? token,
        CancellationToken cancellationToken)
    {
        string url = $"{registryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Head, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        AddManifestAcceptHeaders(request);

        try
        {
            HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw RegistryRateLimitException.FromResponse(response);
            }

            // Registry doesn't support HEAD → fall back to the GET path.
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                var (fallbackDigest, _) = await TryFetchManifestAndCreatedAtAsync(
                    registryUrl, repository, tag, architecture, token, cancellationToken);
                return fallbackDigest;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("Manifest HEAD request failed for {Url} with status {StatusCode}", url, response.StatusCode);
                return null;
            }

            if (response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
            {
                return digestValues.FirstOrDefault();
            }

            // No digest header → fall back to GET.
            var (digest, _) = await TryFetchManifestAndCreatedAtAsync(
                registryUrl, repository, tag, architecture, token, cancellationToken);
            return digest;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "HTTP HEAD request failed for {Url}", url);
            return null;
        }
    }

    public override async Task<(string? Digest, DateTime? CreatedAt)> GetManifestDigestAndCreatedAtAsync(
        string registry,
        string repository,
        string tag,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string registryUrl = $"https://{registry}/v2";

            // First, try without authentication
            var result = await TryFetchManifestAndCreatedAtAsync(
                registryUrl, repository, tag, architecture, null, cancellationToken);

            if (result.Digest != null)
            {
                return result;
            }

            // If that fails, try to get a token via WWW-Authenticate challenge
            Logger.LogDebug("Anonymous access failed for {Registry}/{Repository}, attempting token auth", registry, repository);

            string? token = await GetTokenViaWwwAuthenticateAsync(
                registry, registryUrl, repository, tag, cancellationToken);

            if (token != null)
            {
                return await TryFetchManifestAndCreatedAtAsync(
                    registryUrl, repository, tag, architecture, token, cancellationToken);
            }

            return (null, null);
        }
        catch (RegistryRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting manifest digest for {Registry}/{Repository}:{Tag}", registry, repository, tag);
            return (null, null);
        }
    }

    private async Task<string?> GetTokenViaWwwAuthenticateAsync(
        string registry,
        string registryUrl,
        string repository,
        string tag,
        CancellationToken cancellationToken)
    {
        try
        {
            // Make a request to trigger 401 and get WWW-Authenticate header. Use the requested tag:
            // some registries validate the tag before issuing the challenge, so probing a hardcoded
            // "latest" would fail for repositories that don't have that tag.
            string url = $"{registryUrl}/{repository}/manifests/{tag}";

            using HttpRequestMessage request = new(HttpMethod.Get, url);
            HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return null;
            }

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

            if (!parameters.TryGetValue("realm", out string? realm))
            {
                return null;
            }

            // Build token URL
            string tokenUrl = realm;
            var queryParams = new List<string>();

            if (parameters.TryGetValue("service", out string? service))
            {
                queryParams.Add($"service={Uri.EscapeDataString(service)}");
            }

            if (parameters.TryGetValue("scope", out string? scope))
            {
                queryParams.Add($"scope={Uri.EscapeDataString(scope)}");
            }
            else
            {
                queryParams.Add($"scope=repository:{repository}:pull");
            }

            if (queryParams.Count > 0)
            {
                tokenUrl += (tokenUrl.Contains('?') ? '&' : '?') + string.Join('&', queryParams);
            }

            (string Username, string Password)? credentials = await _credentialService.GetRawCredentialsAsync(registry);
            AuthenticationHeaderValue? basicAuth = null;
            if (credentials.HasValue)
            {
                Logger.LogDebug("Using Docker config credentials for {Registry} (user: {User})",
                    registry, credentials.Value.Username);
                basicAuth = CreateBasicAuthHeader(credentials.Value.Username, credentials.Value.Password);
            }

            return await RequestBearerTokenAsync(tokenUrl, basicAuth, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to get token via WWW-Authenticate");
            return null;
        }
    }

    private async Task<(string? Digest, DateTime? CreatedAt)> TryFetchManifestAndCreatedAtAsync(
        string registryUrl,
        string repository,
        string tag,
        string architecture,
        string? token,
        CancellationToken cancellationToken)
    {
        string url = $"{registryUrl}/{repository}/manifests/{tag}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        AddManifestAcceptHeaders(request);

        try
        {
            HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw RegistryRateLimitException.FromResponse(response);
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("Manifest request failed for {Url} with status {StatusCode}",
                    url, response.StatusCode);
                return (null, null);
            }

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
                // the architecture-specific manifest to get the config for the creation date.
                string? archManifestDigest = ExtractDigestFromManifestList(content, architecture);
                if (archManifestDigest == null)
                {
                    return (digest, null);
                }

                string? configDigest = await FetchConfigDigestFromManifestAsync(registryUrl, repository, archManifestDigest, token, cancellationToken);
                DateTime? createdAt = null;
                if (configDigest != null)
                {
                    createdAt = await FetchConfigCreatedAtAsync(registryUrl, repository, configDigest, token, cancellationToken);
                }
                return (digest, createdAt);
            }

            // Single manifest - extract config digest from manifest body
            string? singleConfigDigest = ExtractConfigDigestFromManifest(content);
            DateTime? singleCreatedAt = null;
            if (singleConfigDigest != null)
            {
                singleCreatedAt = await FetchConfigCreatedAtAsync(registryUrl, repository, singleConfigDigest, token, cancellationToken);
            }

            return (digest, singleCreatedAt);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "HTTP request failed for {Url}", url);
            return (null, null);
        }
    }
}
