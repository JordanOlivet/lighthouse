using YamlDotNet.Serialization;

namespace Lighthouse.Utils;

/// <summary>
/// Pure path logic behind running compose from the host path of the compose directory
/// (issue #219). Compose resolves relative paths (<c>./data:/data</c>) against the compose
/// file's directory and sends the absolute result to the daemon, which interprets it on the
/// host. When Lighthouse sees the compose directory at a different path than the host
/// (<c>/app/compose-files</c> vs <c>/home/user/stacks</c>), relative bind mounts would point to
/// empty host folders. All paths here use POSIX semantics regardless of the OS running the code.
/// </summary>
public static class ComposeHostPathHelper
{
    /// <summary>
    /// Returns the host path backing <paramref name="rootPath"/> from the bind mounts of the
    /// Lighthouse container (the deepest mount whose destination contains it), or null when no
    /// mount covers it or its source is not an absolute POSIX path (e.g. a Windows path).
    /// </summary>
    public static string? ResolveHostRoot(
        IEnumerable<(string? Source, string? Destination)> bindMounts,
        string rootPath)
    {
        string root = Normalize(rootPath);
        string? bestSource = null;
        string? bestDestination = null;

        foreach ((string? source, string? destination) in bindMounts)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
                continue;

            string normalizedDestination = Normalize(destination);
            if (!IsSameOrUnder(root, normalizedDestination))
                continue;

            if (bestDestination == null || normalizedDestination.Length > bestDestination.Length)
            {
                bestSource = source;
                bestDestination = normalizedDestination;
            }
        }

        if (bestSource == null || bestDestination == null)
            return null;

        string normalizedSource = Normalize(bestSource);
        if (!normalizedSource.StartsWith('/'))
            return null;

        string remainder = bestDestination == "/" ? root : root[bestDestination.Length..];
        return Normalize(normalizedSource + remainder);
    }

    /// <summary>
    /// Maps a path under <paramref name="rootPath"/> to the same location under
    /// <paramref name="hostRoot"/>. Paths outside <paramref name="rootPath"/> are returned unchanged.
    /// </summary>
    public static string ToHostPath(string containerPath, string rootPath, string hostRoot)
    {
        string path = Normalize(containerPath);
        string root = Normalize(rootPath);

        if (!IsSameOrUnder(path, root))
            return containerPath;

        string remainder = root == "/" ? path : path[root.Length..];
        return Normalize(Normalize(hostRoot) + remainder);
    }

    /// <summary>
    /// True when one path is the same as, or nested inside, the other.
    /// </summary>
    public static bool PathsOverlap(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        return IsSameOrUnder(na, nb) || IsSameOrUnder(nb, na);
    }

    /// <summary>
    /// Returns the relative bind mount sources (<c>./x</c>, <c>../x</c>, <c>~/x</c>) declared by
    /// the services of <paramref name="composeContent"/>, formatted as <c>service: source</c>.
    /// Only bind mounts matter: the daemon resolves them on the host, whereas <c>env_file</c>,
    /// <c>build</c> and <c>.env</c> are read client-side, inside the Lighthouse container.
    /// Returns an empty list when the YAML cannot be parsed.
    /// </summary>
    public static List<string> GetRelativeBindMounts(string composeContent)
    {
        List<string> result = new();

        Dictionary<object, object>? services;
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();

            Dictionary<string, object>? composeData =
                deserializer.Deserialize<Dictionary<string, object>>(composeContent);

            if (composeData == null || !composeData.TryGetValue("services", out object? servicesObj))
                return result;

            services = servicesObj as Dictionary<object, object>;
        }
        catch
        {
            return result;
        }

        if (services == null)
            return result;

        foreach ((object? serviceKey, object? serviceValue) in services)
        {
            if (serviceValue is not Dictionary<object, object> serviceData
                || !serviceData.TryGetValue("volumes", out object? volumesObj)
                || volumesObj is not List<object> volumes)
            {
                continue;
            }

            foreach (object? volume in volumes)
            {
                string? source = volume switch
                {
                    // Short syntax: "SOURCE:TARGET[:MODE]"
                    string shortSyntax when shortSyntax.Contains(':') => shortSyntax[..shortSyntax.IndexOf(':')],
                    // Long syntax: { type: bind, source: ..., target: ... }
                    Dictionary<object, object> longSyntax
                        when longSyntax.TryGetValue("type", out object? type) && type?.ToString() == "bind"
                        => longSyntax.TryGetValue("source", out object? src) ? src?.ToString() : null,
                    _ => null
                };

                if (source != null && IsRelativeSource(source.Trim()))
                    result.Add($"{serviceKey}: {source.Trim()}");
            }
        }

        return result;
    }

    /// <summary>
    /// Returns an error message when running <paramref name="composeDirectory"/>'s compose file
    /// from Lighthouse would break the project: its containers were created from another
    /// directory (their <c>working_dir</c> label, e.g. started from the host CLI) and the file
    /// uses relative bind mounts, which would then resolve to different, empty host folders.
    /// Returns null when the operation is safe.
    /// </summary>
    public static string? GetRelativeMountConflict(
        string projectName,
        string composeDirectory,
        string composeContent,
        IEnumerable<string?> containerWorkingDirectories)
    {
        string directory = Normalize(composeDirectory);
        string? otherWorkingDir = containerWorkingDirectories
            .Where(wd => !string.IsNullOrWhiteSpace(wd))
            .FirstOrDefault(wd => Normalize(wd!) != directory);

        if (otherWorkingDir == null)
            return null;

        List<string> relativeMounts = GetRelativeBindMounts(composeContent);
        if (relativeMounts.Count == 0)
            return null;

        return $"Project '{projectName}' was started from '{otherWorkingDir}', but Lighthouse sees its compose file in " +
               $"'{directory}'. Its relative bind mounts ({string.Join(", ", relativeMounts)}) would resolve to " +
               "different, empty host folders. Use absolute paths in the compose file, or mount the compose " +
               "directory at the same path in the Lighthouse container.";
    }

    private static bool IsRelativeSource(string source) =>
        source == "." || source == ".." || source == "~"
        || source.StartsWith("./") || source.StartsWith("../") || source.StartsWith("~/");

    private static bool IsSameOrUnder(string path, string parent) =>
        parent == "/"
            ? path.StartsWith('/')
            : path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith('/'))
            normalized = normalized[..^1];
        return normalized;
    }
}
