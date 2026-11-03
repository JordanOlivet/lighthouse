namespace Lighthouse.Services.Notifications;

/// <summary>Text formatting helpers shared by the notification channels.</summary>
internal static class NotificationFormatting
{
    /// <summary>Shortens a sha256 reference to 12 chars; leaves repo:tag names as-is.</summary>
    public static string FormatImageRef(string entry)
    {
        const string prefix = "sha256:";
        if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string hex = entry[prefix.Length..];
            return hex.Length > 12 ? hex[..12] : hex;
        }
        return entry;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1)
        {
            size /= 1024;
            u++;
        }
        return $"{size:0.##} {units[u]}";
    }

    public static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return max <= 1 ? value[..max] : value[..(max - 1)] + "…";
    }
}
