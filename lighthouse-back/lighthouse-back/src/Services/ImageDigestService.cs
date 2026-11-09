using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Lighthouse.Services;

/// <summary>
/// Service for checking image digests and determining if updates are available.
/// </summary>
public interface IImageDigestService
{
    /// <summary>
    /// Gets the local digest for an image.
    /// </summary>
    Task<ImageDigestInfo> GetLocalDigestAsync(string image, CancellationToken ct = default);

    /// <summary>
    /// Gets the remote digest for an image from its registry.
    /// </summary>
    Task<ImageDigestInfo> GetRemoteDigestAsync(string image, string architecture, CancellationToken ct = default);

    /// <summary>
    /// Checks if an update is available for an image.
    /// </summary>
    Task<ImageUpdateStatus> CheckImageUpdateAsync(string image, string serviceName, CancellationToken ct = default);

    /// <summary>
    /// Gets the host architecture in Docker format (amd64, arm64, etc.).
    /// </summary>
    Task<string> GetHostArchitectureAsync(CancellationToken ct = default);
}

/// <summary>
/// Information about an image's digest.
/// </summary>
/// <param name="RepoDigests">
/// Every registry digest known locally for the image. A single local image can carry several
/// (e.g. when a tag was re-published with a new index for the same content), so update checks
/// must compare the remote digest against all of them, not just <see cref="Digest"/>.
/// </param>
public record ImageDigestInfo(
    string Image,
    string? Digest,
    string? Architecture,
    DateTime? CreatedAt,
    bool IsLocalBuild,
    bool IsPinnedDigest,
    string? Error,
    IReadOnlyList<string>? RepoDigests = null
)
{
    /// <summary>
    /// True when <paramref name="remoteDigest"/> is one of this image's local digests.
    /// </summary>
    public bool HasDigest(string remoteDigest)
    {
        if (string.Equals(Digest, remoteDigest, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RepoDigests != null &&
               RepoDigests.Any(d => string.Equals(d, remoteDigest, StringComparison.OrdinalIgnoreCase));
    }
}

public class ImageDigestService : IImageDigestService
{
    private readonly DockerCommandExecutorService _dockerExecutor;
    private readonly IRegistryClientFactory _registryClientFactory;
    private readonly IRegistryRateLimitGate _rateLimitGate;
    private readonly ILogger<ImageDigestService> _logger;
    private readonly UpdateCheckOptions _options;

    private string? _cachedHostArchitecture;

    public ImageDigestService(
        DockerCommandExecutorService dockerExecutor,
        IRegistryClientFactory registryClientFactory,
        IRegistryRateLimitGate rateLimitGate,
        IOptions<UpdateCheckOptions> options,
        ILogger<ImageDigestService> logger)
    {
        _dockerExecutor = dockerExecutor;
        _registryClientFactory = registryClientFactory;
        _rateLimitGate = rateLimitGate;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetHostArchitectureAsync(CancellationToken ct = default)
    {
        if (_cachedHostArchitecture != null)
        {
            return _cachedHostArchitecture;
        }

        try
        {
            // Get architecture from docker info
            (int exitCode, string output, string error) = await _dockerExecutor.ExecuteAsync(
                "docker", "info --format '{{.Architecture}}'", ct);

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                string arch = output.Trim().Trim('\'', '"');
                _cachedHostArchitecture = MapArchitecture(arch);
                return _cachedHostArchitecture;
            }

            // Fallback to system architecture
            string systemArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            _cachedHostArchitecture = MapArchitecture(systemArch);
            return _cachedHostArchitecture;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get host architecture, defaulting to amd64");
            return "amd64";
        }
    }

    private string MapArchitecture(string arch)
    {
        return arch.ToLowerInvariant() switch
        {
            "x86_64" => "amd64",
            "x64" => "amd64",
            "amd64" => "amd64",
            "aarch64" => "arm64",
            "arm64" => "arm64",
            "armv7l" => "arm/v7",
            "arm" => "arm/v7",
            _ => arch.ToLowerInvariant()
        };
    }

    public async Task<ImageDigestInfo> GetLocalDigestAsync(string image, CancellationToken ct = default)
    {
        try
        {
            // Check if image is pinned to a digest
            if (image.Contains('@'))
            {
                string pinnedDigest = image.Substring(image.IndexOf('@') + 1);
                return new ImageDigestInfo(
                    Image: image,
                    Digest: pinnedDigest,
                    Architecture: null,
                    CreatedAt: null,
                    IsLocalBuild: false,
                    IsPinnedDigest: true,
                    Error: null
                );
            }

            // Get the image inspect data
            (int exitCode, string output, string error) = await _dockerExecutor.ExecuteAsync(
                "docker", $"image inspect {image} --format json", ct);

            if (exitCode != 0)
            {
                // Image might not exist locally
                return new ImageDigestInfo(
                    Image: image,
                    Digest: null,
                    Architecture: null,
                    CreatedAt: null,
                    IsLocalBuild: false,
                    IsPinnedDigest: false,
                    Error: $"Image not found locally: {error}"
                );
            }

            // Parse JSON output (it's an array)
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return new ImageDigestInfo(
                    Image: image,
                    Digest: null,
                    Architecture: null,
                    CreatedAt: null,
                    IsLocalBuild: false,
                    IsPinnedDigest: false,
                    Error: "Invalid inspect output"
                );
            }

            JsonElement imageData = root[0];

            // Get RepoDigests (entries like "nginx@sha256:abc123..."). An image can hold several,
            // e.g. after a tag was re-published with a new index for identical content.
            List<string> digests = new();
            if (imageData.TryGetProperty("RepoDigests", out JsonElement repoDigests) &&
                repoDigests.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in repoDigests.EnumerateArray())
                {
                    string? repoDigest = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                    if (repoDigest != null && repoDigest.Contains('@'))
                    {
                        string d = repoDigest.Substring(repoDigest.IndexOf('@') + 1);
                        if (!digests.Contains(d, StringComparer.OrdinalIgnoreCase))
                        {
                            digests.Add(d);
                        }
                    }
                }
            }

            string? digest = digests.Count > 0 ? digests[0] : null;
            bool isLocalBuild = digests.Count == 0;

            // Get architecture
            string? architecture = null;
            if (imageData.TryGetProperty("Architecture", out JsonElement archElement))
            {
                architecture = archElement.GetString();
            }

            // Get created date
            DateTime? createdAt = null;
            if (imageData.TryGetProperty("Created", out JsonElement createdElement))
            {
                string? createdStr = createdElement.GetString();
                if (!string.IsNullOrEmpty(createdStr) && DateTime.TryParse(createdStr, out DateTime parsed))
                {
                    createdAt = parsed;
                }
            }

            return new ImageDigestInfo(
                Image: image,
                Digest: digest,
                Architecture: architecture,
                CreatedAt: createdAt,
                IsLocalBuild: isLocalBuild,
                IsPinnedDigest: false,
                Error: null,
                RepoDigests: digests
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting local digest for {Image}", image);
            return new ImageDigestInfo(
                Image: image,
                Digest: null,
                Architecture: null,
                CreatedAt: null,
                IsLocalBuild: false,
                IsPinnedDigest: false,
                Error: ex.Message
            );
        }
    }

    public Task<ImageDigestInfo> GetRemoteDigestAsync(string image, string architecture, CancellationToken ct = default)
        => GetRemoteDigestAsync(image, architecture, includeCreatedAt: false, ct);

    /// <summary>
    /// Fetches the remote digest. By default uses a HEAD request (digest only, not counted against
    /// Docker Hub's pull-rate limit); pass <paramref name="includeCreatedAt"/> to use a GET that also
    /// returns the image creation date — only worth doing once an update has been detected.
    /// Rethrows <see cref="RegistryRateLimitException"/> so the caller can trip the cooldown gate.
    /// </summary>
    private async Task<ImageDigestInfo> GetRemoteDigestAsync(
        string image, string architecture, bool includeCreatedAt, CancellationToken ct)
    {
        try
        {
            // Check if pinned to digest
            if (image.Contains('@'))
            {
                return new ImageDigestInfo(
                    Image: image,
                    Digest: image.Substring(image.IndexOf('@') + 1),
                    Architecture: architecture,
                    CreatedAt: null,
                    IsLocalBuild: false,
                    IsPinnedDigest: true,
                    Error: null
                );
            }

            // Parse image reference
            ImageReference imageRef = _registryClientFactory.ParseImageReference(image);

            // Get appropriate registry client
            IRegistryClient client = _registryClientFactory.GetClient(imageRef.Registry);

            string? digest;
            DateTime? createdAt = null;
            if (includeCreatedAt)
            {
                // GET: also returns the creation date (counts against the pull-rate limit).
                (digest, createdAt) = await client.GetManifestDigestAndCreatedAtAsync(
                    imageRef.Registry, imageRef.Repository, imageRef.Tag, architecture, ct);
            }
            else
            {
                // HEAD: digest only, not counted against the pull-rate limit.
                digest = await client.GetManifestDigestAsync(
                    imageRef.Registry, imageRef.Repository, imageRef.Tag, architecture, ct);
            }

            return new ImageDigestInfo(
                Image: image,
                Digest: digest,
                Architecture: architecture,
                CreatedAt: createdAt,
                IsLocalBuild: false,
                IsPinnedDigest: false,
                Error: digest == null ? "Failed to fetch remote digest" : null
            );
        }
        catch (RegistryRateLimitException)
        {
            // Surfaced to CheckImageUpdateAsync, which trips the shared cooldown gate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting remote digest for {Image}", image);
            return new ImageDigestInfo(
                Image: image,
                Digest: null,
                Architecture: architecture,
                CreatedAt: null,
                IsLocalBuild: false,
                IsPinnedDigest: false,
                Error: ex.Message
            );
        }
    }

    public async Task<ImageUpdateStatus> CheckImageUpdateAsync(string image, string serviceName, CancellationToken ct = default)
    {
        try
        {
            // Get host architecture
            string hostArch = await GetHostArchitectureAsync(ct);

            // Get local digest
            ImageDigestInfo localInfo = await GetLocalDigestAsync(image, ct);

            // If it's a local build or pinned digest, no update check needed
            if (localInfo.IsLocalBuild)
            {
                return new ImageUpdateStatus(
                    Image: image,
                    ServiceName: serviceName,
                    HostArchitecture: hostArch,
                    LocalDigest: null,
                    RemoteDigest: null,
                    LocalCreatedAt: localInfo.CreatedAt,
                    RemoteCreatedAt: null,
                    UpdateAvailable: false,
                    MultiArchSupported: false,
                    UpdatePolicy: null,
                    IsLocalBuild: true,
                    IsPinnedDigest: false,
                    Error: null
                );
            }

            if (localInfo.IsPinnedDigest)
            {
                return new ImageUpdateStatus(
                    Image: image,
                    ServiceName: serviceName,
                    HostArchitecture: hostArch,
                    LocalDigest: localInfo.Digest,
                    RemoteDigest: null,
                    LocalCreatedAt: localInfo.CreatedAt,
                    RemoteCreatedAt: null,
                    UpdateAvailable: false,
                    MultiArchSupported: false,
                    UpdatePolicy: null,
                    IsLocalBuild: false,
                    IsPinnedDigest: true,
                    Error: null
                );
            }

            // Skip the registry entirely while a rate-limit cooldown is active (set after a 429),
            // so a burst of checks doesn't keep extending the ban.
            if (_rateLimitGate.IsCoolingDown(out TimeSpan remaining))
            {
                _logger.LogDebug(
                    "Skipping remote digest for {Image}: registry cooling down ({Seconds:0}s left)",
                    image, remaining.TotalSeconds);
                return RateLimitedStatus(image, serviceName, hostArch, localInfo);
            }

            // Get remote digest via HEAD (not counted against the pull-rate limit)
            ImageDigestInfo remoteInfo;
            try
            {
                remoteInfo = await GetRemoteDigestAsync(image, hostArch, includeCreatedAt: false, ct);
            }
            catch (RegistryRateLimitException ex)
            {
                _rateLimitGate.Trip(ex.RetryAfter);
                _logger.LogWarning("Registry rate limit reached while checking {Image}; pausing remote checks", image);
                return RateLimitedStatus(image, serviceName, hostArch, localInfo);
            }

            // Compare digests: up to date if the remote digest is any of the local ones.
            bool updateAvailable = false;
            string? localDigest = localInfo.Digest;
            if (localInfo.Digest != null && remoteInfo.Digest != null)
            {
                updateAvailable = !localInfo.HasDigest(remoteInfo.Digest);
                if (!updateAvailable)
                {
                    // Report the digest that actually matched so local/remote display as equal.
                    localDigest = remoteInfo.Digest;
                }
            }

            // Determine if multi-arch is supported (we got a valid remote digest)
            bool multiArchSupported = remoteInfo.Digest != null;

            // The HEAD check above does not fetch the creation date; only when an update is detected
            // do we spend one GET (counted) request to populate RemoteCreatedAt for display.
            DateTime? remoteCreatedAt = null;
            if (updateAvailable)
            {
                try
                {
                    ImageDigestInfo withCreated = await GetRemoteDigestAsync(image, hostArch, includeCreatedAt: true, ct);
                    remoteCreatedAt = withCreated.CreatedAt;
                }
                catch (RegistryRateLimitException ex)
                {
                    // The update itself is still valid; just skip the (optional) creation date.
                    _rateLimitGate.Trip(ex.RetryAfter);
                }
            }

            string? error = localInfo.Error ?? remoteInfo.Error;

            return new ImageUpdateStatus(
                Image: image,
                ServiceName: serviceName,
                HostArchitecture: hostArch,
                LocalDigest: localDigest,
                RemoteDigest: remoteInfo.Digest,
                LocalCreatedAt: localInfo.CreatedAt,
                RemoteCreatedAt: remoteCreatedAt,
                UpdateAvailable: updateAvailable,
                MultiArchSupported: multiArchSupported,
                UpdatePolicy: null, // Will be set by ComposeUpdateService based on x-update-policy
                IsLocalBuild: false,
                IsPinnedDigest: false,
                Error: error
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking update for {Image}", image);
            string hostArch = await GetHostArchitectureAsync(ct);

            return new ImageUpdateStatus(
                Image: image,
                ServiceName: serviceName,
                HostArchitecture: hostArch,
                LocalDigest: null,
                RemoteDigest: null,
                LocalCreatedAt: null,
                RemoteCreatedAt: null,
                UpdateAvailable: false,
                MultiArchSupported: false,
                UpdatePolicy: null,
                IsLocalBuild: false,
                IsPinnedDigest: false,
                Error: ex.Message
            );
        }
    }

    /// <summary>
    /// Builds an update status for an image that could not be checked because the registry is
    /// rate-limiting us. Keeps the local digest so the project stays visible; marks no update.
    /// </summary>
    private static ImageUpdateStatus RateLimitedStatus(
        string image, string serviceName, string hostArch, ImageDigestInfo localInfo)
    {
        return new ImageUpdateStatus(
            Image: image,
            ServiceName: serviceName,
            HostArchitecture: hostArch,
            LocalDigest: localInfo.Digest,
            RemoteDigest: null,
            LocalCreatedAt: localInfo.CreatedAt,
            RemoteCreatedAt: null,
            UpdateAvailable: false,
            MultiArchSupported: false,
            UpdatePolicy: null,
            IsLocalBuild: false,
            IsPinnedDigest: false,
            Error: "Registry rate limited; will retry after cooldown"
        );
    }
}
