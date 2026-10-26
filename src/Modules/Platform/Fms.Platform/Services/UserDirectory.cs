using Fms.BuildingBlocks.Identity;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

public sealed class UserDirectory(PlatformDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, DirectoryUser>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, DirectoryUser>();
        var users = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToListAsync(ct);
        return users.ToDictionary(u => u.Id, u => new DirectoryUser(u.Id, u.DisplayName, u.Email, u.IsActive));
    }

    public async Task<IReadOnlyList<DirectoryUser>> WithPermissionAsync(string permission, CancellationToken ct = default)
    {
        var ids = await (from ur in db.UserRoles
                         join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                         where rp.Permission == permission
                         select ur.UserId).Distinct().ToListAsync(ct);
        return (await db.Users.AsNoTracking().Where(u => u.IsActive && ids.Contains(u.Id)).OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync(ct))
            .Select(u => new DirectoryUser(u.Id, u.DisplayName, u.Email, u.IsActive)).ToList();
    }
}
