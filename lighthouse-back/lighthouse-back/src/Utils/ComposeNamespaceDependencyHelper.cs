using YamlDotNet.Serialization;

namespace Lighthouse.Utils;

/// <summary>
/// Resolves compose services that share another service's namespace via
/// <c>network_mode</c>, <c>ipc</c> or <c>pid</c> set to <c>service:&lt;name&gt;</c>
/// (or <c>container:&lt;name&gt;</c> when the referenced container belongs to a
/// service of the same file). Recreating the parent container destroys the shared
/// namespace, so these dependents must be recreated together with it — otherwise
/// they stay attached to the dead namespace of the removed container (issue #211).
/// </summary>
public static class ComposeNamespaceDependencyHelper
{
    private static readonly string[] NamespaceKeys = { "network_mode", "ipc", "pid" };

    /// <summary>
    /// Returns the services of <paramref name="composeContent"/> that (directly or
    /// transitively) share a namespace with any of <paramref name="updatedServices"/>,
    /// excluding the updated services themselves. Returns an empty list when the YAML
    /// cannot be parsed or declares no namespace sharing.
    /// </summary>
    public static List<string> GetDependentServices(
        string composeContent,
        IReadOnlyCollection<string> updatedServices)
    {
        Dictionary<string, List<string>> dependentsByParent = BuildDependentsByParentMap(composeContent);
        if (dependentsByParent.Count == 0)
            return new List<string>();

        // Walk the dependency edges transitively: if A shares B's namespace and B shares C's,
        // updating C must recreate both B and A.
        HashSet<string> affected = new(updatedServices, StringComparer.Ordinal);
        Queue<string> toVisit = new(updatedServices);
        while (toVisit.Count > 0)
        {
            string parent = toVisit.Dequeue();
            if (!dependentsByParent.TryGetValue(parent, out List<string>? dependents))
                continue;

            foreach (string dependent in dependents)
            {
                if (affected.Add(dependent))
                    toVisit.Enqueue(dependent);
            }
        }

        return affected
            .Where(s => !updatedServices.Contains(s, StringComparer.Ordinal))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Maps each parent service to the services that join one of its namespaces.
    /// </summary>
    private static Dictionary<string, List<string>> BuildDependentsByParentMap(string composeContent)
    {
        Dictionary<string, List<string>> map = new(StringComparer.Ordinal);

        Dictionary<string, Dictionary<object, object>> services = ParseServices(composeContent);
        if (services.Count == 0)
            return map;

        // container:<name> references a container by name; resolve it to a service of the
        // same file via its container_name (or the service name, compose's default prefix
        // aside, which we can only match exactly).
        Dictionary<string, string> containerNameToService = new(StringComparer.Ordinal);
        foreach ((string serviceName, Dictionary<object, object> serviceData) in services)
        {
            if (serviceData.TryGetValue("container_name", out object? containerNameObj)
                && containerNameObj?.ToString() is { Length: > 0 } containerName)
            {
                containerNameToService[containerName] = serviceName;
            }
        }

        foreach ((string serviceName, Dictionary<object, object> serviceData) in services)
        {
            foreach (string key in NamespaceKeys)
            {
                if (!serviceData.TryGetValue(key, out object? valueObj))
                    continue;

                string? parentService = ResolveParentService(
                    valueObj?.ToString(), services, containerNameToService);

                if (parentService == null || parentService == serviceName)
                    continue;

                if (!map.TryGetValue(parentService, out List<string>? dependents))
                {
                    dependents = new List<string>();
                    map[parentService] = dependents;
                }
                if (!dependents.Contains(serviceName, StringComparer.Ordinal))
                    dependents.Add(serviceName);
            }
        }

        return map;
    }

    private static string? ResolveParentService(
        string? value,
        Dictionary<string, Dictionary<object, object>> services,
        Dictionary<string, string> containerNameToService)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();

        if (trimmed.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
        {
            string name = trimmed["service:".Length..].Trim();
            return services.ContainsKey(name) ? name : null;
        }

        if (trimmed.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            string name = trimmed["container:".Length..].Trim();
            if (containerNameToService.TryGetValue(name, out string? serviceName))
                return serviceName;
            return services.ContainsKey(name) ? name : null;
        }

        return null;
    }

    private static Dictionary<string, Dictionary<object, object>> ParseServices(string composeContent)
    {
        Dictionary<string, Dictionary<object, object>> result = new(StringComparer.Ordinal);

        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();

            Dictionary<string, object>? composeData =
                deserializer.Deserialize<Dictionary<string, object>>(composeContent);

            if (composeData == null
                || !composeData.TryGetValue("services", out object? servicesObj)
                || servicesObj is not Dictionary<object, object> services)
            {
                return result;
            }

            foreach ((object? serviceKey, object? serviceValue) in services)
            {
                string serviceName = serviceKey?.ToString() ?? "";
                if (serviceName.Length > 0 && serviceValue is Dictionary<object, object> serviceData)
                    result[serviceName] = serviceData;
            }
        }
        catch
        {
            // Unparseable YAML: treated as "no namespace dependencies"; the caller logs.
        }

        return result;
    }
}
