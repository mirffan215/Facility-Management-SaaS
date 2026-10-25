using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Security;
using Fms.Platform.Domain;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class AccessControlTests(SqlServerFixture sql)
{
    private sealed class FakeUser(Guid? userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? Subject => "x";
        public string? Provider => "entra";
        public Guid? TenantId => null;
        public Guid? UserId { get; } = userId;
    }

    private AccessAdministration Access(Fms.Platform.Persistence.PlatformDbContext db, Guid? actor) =>
        new(db, new FakeUser(actor), new Fms.BuildingBlocks.Audit.AuditPort(sql.PendingFor(db), new FakeUser(actor), TimeProvider.System), TimeProvider.System);

    private async Task<Guid> NewUserAsync(Guid tenant)
    {
        await using var db = sql.App(tenant);
        var user = NewUser("u-" + Guid.NewGuid().ToString("N"));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> RoleIdAsync(Guid tenant, string name)
    {
        await using var db = sql.App(tenant);
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task System_roles_exist_per_tenant_and_sync_is_idempotent()
    {
        await using (var platform = sql.App(null, platform: true))
        {
            await SystemRoleSync.EnsureAsync(platform, TenantA);
        }

        await using var a = sql.App(TenantA);
        var roles = await Access(a, null).ListRolesAsync();

        Assert.Equal(SystemRoles.Definitions.Count, roles.Count);
        Assert.Equal(Permissions.All.Order(), roles.Single(r => r.Name == SystemRoles.Administrator).Permissions);
        Assert.All(roles, r => Assert.True(r.IsSystem));
    }

    [Fact]
    public async Task Roles_and_assignments_are_tenant_isolated_in_the_database()
    {
        await using var a = sql.App(TenantA);

        var foreign = await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Roles WHERE TenantId = {TenantB}").SingleAsync();
        var foreignPerms = await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.RolePermissions WHERE TenantId = {TenantB}").SingleAsync();
        var foreignLinks = await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.UserRoles WHERE TenantId = {TenantB}").SingleAsync();

        Assert.Equal(0, foreign + foreignPerms + foreignLinks);
    }

    [Fact]
    public async Task Admin_can_assign_roles_and_the_change_is_audited_atomically()
    {
        var admin = await NewUserAsync(TenantA);
        var target = await NewUserAsync(TenantA);
        var role = await RoleIdAsync(TenantA, SystemRoles.Technician);

        await using (var db = sql.App(TenantA))
        {
            Assert.Equal(AccessResult.Ok, await Access(db, admin).SetUserRolesAsync(target, [role]));
        }

        await using var check = sql.App(TenantA);
        Assert.Equal([role], (await check.UserRoles.Where(l => l.UserId == target).ToListAsync()).Select(l => l.RoleId));
        var audit = await check.AuditEvents.Where(e => e.EntityId == target.ToString() && e.Action == "user.roles.changed").ToListAsync();
        Assert.Single(audit);
        Assert.Equal(admin, audit[0].ActorId);
    }

    [Fact]
    public async Task Replacing_roles_removes_the_old_ones()
    {
        var actor = await NewUserAsync(TenantA);
        var target = await NewUserAsync(TenantA);
        var tech = await RoleIdAsync(TenantA, SystemRoles.Technician);
        var viewer = await RoleIdAsync(TenantA, SystemRoles.Viewer);

        await using (var db = sql.App(TenantA)) await Access(db, actor).SetUserRolesAsync(target, [tech]);
        await using (var db = sql.App(TenantA)) await Access(db, actor).SetUserRolesAsync(target, [viewer]);

        await using var check = sql.App(TenantA);
        Assert.Equal([viewer], await check.UserRoles.Where(l => l.UserId == target).Select(l => l.RoleId).ToListAsync());
    }

    [Fact]
    public async Task Nobody_can_change_their_own_roles()
    {
        var me = await NewUserAsync(TenantA);
        var adminRole = await RoleIdAsync(TenantA, SystemRoles.Administrator);
        await using var db = sql.App(TenantA);

        Assert.Equal(AccessResult.CannotChangeOwnRoles, await Access(db, me).SetUserRolesAsync(me, [adminRole]));
        Assert.False(await db.UserRoles.AnyAsync(l => l.UserId == me));
    }

    [Fact]
    public async Task Roles_of_another_tenant_are_rejected()
    {
        var actor = await NewUserAsync(TenantA);
        var target = await NewUserAsync(TenantA);
        var foreignRole = await RoleIdAsync(TenantB, SystemRoles.Administrator);
        await using var db = sql.App(TenantA);

        Assert.Equal(AccessResult.InvalidRole, await Access(db, actor).SetUserRolesAsync(target, [foreignRole]));
        Assert.Equal(AccessResult.InvalidRole, await Access(db, actor).SetUserRolesAsync(target, [Guid.NewGuid()]));
    }

    [Fact]
    public async Task A_user_of_another_tenant_cannot_be_changed()
    {
        var actor = await NewUserAsync(TenantA);
        var otherTenantUser = await NewUserAsync(TenantB);
        var role = await RoleIdAsync(TenantA, SystemRoles.Viewer);
        await using var db = sql.App(TenantA);

        Assert.Equal(AccessResult.NotFound, await Access(db, actor).SetUserRolesAsync(otherTenantUser, [role]));
    }

    [Fact]
    public async Task Permissions_are_the_union_of_the_users_roles()
    {
        var actor = await NewUserAsync(TenantA);
        var target = await NewUserAsync(TenantA);
        await using (var db = sql.App(TenantA))
        {
            await Access(db, actor).SetUserRolesAsync(target,
                [await RoleIdAsync(TenantA, SystemRoles.FacilityManager), await RoleIdAsync(TenantA, SystemRoles.Technician)]);
        }

        await using var read = sql.App(TenantA);
        var perms = await new PermissionService(read, new FakeUser(target)).GetCurrentAsync();

        Assert.Equal([Permissions.RolesRead, Permissions.UsersRead], perms.Order());
    }
}
