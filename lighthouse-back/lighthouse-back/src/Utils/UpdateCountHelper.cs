using Lighthouse.DTOs;

namespace Lighthouse.Utils;

/// <summary>
/// Counts the services of a project that have an image update available.
/// </summary>
public static class UpdateCountHelper
{
    /// <summary>
    /// Number of services with an available update. A one-shot service whose image is shared
    /// with another (non one-shot) service that also has the update is not counted: one pull
    /// updates both, and the one-shot re-runs as part of that same update.
    /// </summary>
    /// <param name="images">Per-service update statuses of one project.</param>
    /// <param name="excludeDisabled">Skip services whose update policy is <c>disabled</c>.</param>
    public static int CountServicesWithUpdates(IEnumerable<ImageUpdateStatus> images, bool excludeDisabled = true)
    {
        List<ImageUpdateStatus> updated = images
            .Where(i => i.UpdateAvailable && (!excludeDisabled || i.UpdatePolicy != "disabled"))
            .ToList();

        HashSet<string> imagesOfRegularServices = updated
            .Where(i => !i.IsOneShot)
            .Select(i => i.Image)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return updated.Count(i => !i.IsOneShot || !imagesOfRegularServices.Contains(i.Image));
    }
}
