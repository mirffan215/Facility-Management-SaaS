using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Fms.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

public sealed record RoleInfo(Guid Id, string Name, bool IsSystem, IReadOnlyList<string> Permissions);

public sealed record UserInfo(
    Guid Id, string? Email, string? DisplayName, bool IsActive, IReadOnlyList<Guid> RoleIds, string? Provider = null, DateTimeOffset? LastSignInAt = null);

public enum AccessResult { Ok, NotFound, InvalidRole, CannotChangeOwnRoles, CannotDeactivateSelf }

public interface IPermissionService
{
    /// <summary>Permissions of the current signed-in user (empty when none). Loaded once per request.</summary>
    Task<IReadOnlySet<string>> GetCurrentAsync(CancellationToken ct = default);
}

public sealed class PermissionService(PlatformDbContext db, ICurrentUser user) : IPermissionService
{
    private IReadOnlySet<string>? _cached;

    public async Task<IReadOnlySet<string>> GetCurrentAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        if (user.UserId is not { } userId) return _cached = new HashSet<string>();

        _cached = (await (from ur in db.UserRoles
                          join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                          where ur.UserId == userId
                          select rp.Permission).Distinct().ToListAsync(ct)).ToHashSet();
        return _cached;
    }
}

public sealed class AccessAdministration(PlatformDbContext db, ICurrentUser current, IAuditPort audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        var perms = await db.RolePermissions.ToListAsync(ct);
        return roles.Select(r => new RoleInfo(r.Id, r.Name, r.IsSystem,
            perms.Where(p => p.RoleId == r.Id).Select(p => p.Permission).OrderBy(p => p).ToList())).ToList();
    }

    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct = default)
    {
        var users = await db.Users.OrderBy(u => u.Email).ThenBy(u => u.Id).ToListAsync(ct);
        var links = await db.UserRoles.ToListAsync(ct);
        return users.Select(u => new UserInfo(u.Id, u.Email, u.DisplayName, u.IsActive,
            links.Where(l => l.UserId == u.Id).Select(l => l.RoleId).ToList(), u.Provider, u.LastSignInAt)).ToList();
    }

    /// <summary>
    /// Turns a person's access off or on. A deactivated user is refused on their very next request (the sign-in token stays valid, the
    /// database says no) and keeps their history and roles. You cannot deactivate yourself.
    /// </summary>
    public async Task<AccessResult> SetUserActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        if (!active && current.UserId == userId) return AccessResult.CannotDeactivateSelf;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AccessResult.NotFound;
        if (user.IsActive == active) return AccessResult.Ok;

        user.IsActive = active;
        audit.Record(active ? "user.reactivated" : "user.deactivated", nameof(User), userId.ToString());
        await db.SaveChangesAsync(ct);
        return AccessResult.Ok;
    }

    /// <summary>
    /// Replaces a user's roles. You cannot change your own roles (no self-escalation); only roles of this tenant are accepted.
    /// The change and its audit record are saved in one transaction.
    /// </summary>
    public async Task<AccessResult> SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct = default)
    {
        if (current.UserId == userId) return AccessResult.CannotChangeOwnRoles;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AccessResult.NotFound;

        var wanted = roleIds.Distinct().ToList();
        var valid = await db.Roles.Where(r => wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (valid.Count != wanted.Count) return AccessResult.InvalidRole;

        var existing = await db.UserRoles.Where(l => l.UserId == userId).ToListAsync(ct);
        db.UserRoles.RemoveRange(existing.Where(l => !wanted.Contains(l.RoleId)));
        foreach (var roleId in wanted.Where(w => existing.All(l => l.RoleId != w)))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, GrantedBy = current.UserId, GrantedAt = clock.GetUtcNow() });
        }

        audit.Record("user.roles.changed", nameof(User), userId.ToString(),
            System.Text.Json.JsonSerializer.Serialize(new { roles = wanted }));
        await db.SaveChangesAsync(ct);
        return AccessResult.Ok;
    }
}

/// <summary>Keeps each tenant's system roles equal to <see cref="SystemRoles.Definitions"/>. Idempotent; run by the migrator.</summary>
public static class SystemRoleSync
{
    public static async Task EnsureAsync(PlatformDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        foreach (var (name, permissions) in SystemRoles.Definitions)
        {
            var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == name, ct);
            if (role is null)
            {
                role = new Role { TenantId = tenantId, Name = name, IsSystem = true };
                db.Roles.Add(role);
            }

            var have = await db.RolePermissions.IgnoreQueryFilters().Where(p => p.RoleId == role.Id).ToListAsync(ct);
            db.RolePermissions.RemoveRange(have.Where(p => !permissions.Contains(p.Permission)));
            foreach (var perm in permissions.Where(p => have.All(h => h.Permission != p)))
            {
                db.RolePermissions.Add(new RolePermission { TenantId = tenantId, RoleId = role.Id, Permission = perm });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
