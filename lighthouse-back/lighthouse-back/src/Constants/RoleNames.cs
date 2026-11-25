namespace Lighthouse.Constants;

public static class RoleNames
{
    public const string Admin = "admin";

    /// <summary>Same rule as PermissionService: the admin role name is matched case-insensitively.</summary>
    public static bool IsAdmin(string? roleName) =>
        string.Equals(roleName, Admin, StringComparison.OrdinalIgnoreCase);
}
