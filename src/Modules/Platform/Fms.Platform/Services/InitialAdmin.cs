using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Fms.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

/// <summary>Bootstrap: creates (or finds) a user by provider and subject and grants the Administrator role.</summary>
public static class InitialAdmin
{
    public static async Task EnsureAsync(PlatformDbContext db, Guid tenantId, string provider, string subject)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Provider == provider && u.Subject == subject);
        if (user is null)
        {
            user = new User { TenantId = tenantId, Provider = provider, Subject = subject, CreatedAt = DateTimeOffset.UtcNow, LastSignInAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
        }

        var role = await db.Roles.IgnoreQueryFilters().FirstAsync(r => r.TenantId == tenantId && r.Name == SystemRoles.Administrator);
        if (!await db.UserRoles.IgnoreQueryFilters().AnyAsync(l => l.UserId == user.Id && l.RoleId == role.Id))
        {
            db.UserRoles.Add(new UserRole { TenantId = tenantId, UserId = user.Id, RoleId = role.Id, GrantedAt = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
    }
}
