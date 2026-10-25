namespace Fms.BuildingBlocks.Security;

/// <summary>
/// Every permission the product knows. A permission is "area.action". Modules add their own constants here as they are built
/// and list them in <see cref="All"/>; system roles are defined from these in <c>SystemRoles</c>.
/// </summary>
public static class Permissions
{
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string RolesRead = "roles.read";
    public const string AuditRead = "audit.read";

    public static IReadOnlyList<string> All { get; } = [UsersRead, UsersManage, RolesRead, AuditRead];

    public static string PolicyName(string permission) => "perm:" + permission;
}

/// <summary>Records who did what, in the same database transaction as the change itself.</summary>
public interface IAuditPort
{
    void Record(string action, string entityType, string? entityId = null, string? changeSummaryJson = null);
}
