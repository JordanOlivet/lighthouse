using Docker.DotNet.Models;

namespace Lighthouse.Services;

/// <summary>
/// Narrow seam over the raw Docker.DotNet image/container calls needed by
/// <see cref="ImageService"/>. Implemented by <see cref="DockerService"/>.
/// Exists so the image orchestration logic (mapping, in-use detection,
/// self-image protection, guards) can be unit-tested without a real Docker
/// daemon — <see cref="DockerService"/> itself builds its client internally and
/// is not mockable.
/// </summary>
public interface IDockerImageOperations
{
    /// <summary>
    /// Lists top-level images (equivalent to <c>docker images</c>).
    /// </summary>
    Task<IList<ImagesListResponse>> ListImagesRawAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all containers (running and stopped) so callers can map images to
    /// the containers that reference them.
    /// </summary>
    Task<IList<ContainerListResponse>> ListContainersRawAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes an image by ID or tag. Returns false if the image was not found.
    /// </summary>
    Task<bool> DeleteImageRawAsync(string id, bool force, CancellationToken ct = default);

    /// <summary>
    /// Prunes unused images. When <paramref name="danglingOnly"/> is true only
    /// untagged images are removed; otherwise all images unused by any container.
    /// </summary>
    Task<ImagesPruneResponse> PruneImagesRawAsync(bool danglingOnly, CancellationToken ct = default);

    /// <summary>
    /// Returns the resolved image ID (sha256) backing the given container, or
    /// null if the container cannot be inspected.
    /// </summary>
    Task<string?> GetContainerImageIdRawAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Resolves which of <paramref name="containers"/> are one-shots (see
    /// <see cref="Lighthouse.Utils.OneShotDetector"/>), keyed by container ID.
    /// </summary>
    Task<Dictionary<string, Lighthouse.Utils.OneShotInfo>> ResolveOneShotsRawAsync(
        IEnumerable<ContainerListResponse> containers, CancellationToken ct = default);
}
